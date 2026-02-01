#if DEBUG
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using TabbySSH.Utils;

namespace TabbySSH.Views;

    public partial class DebugPanel : UserControl
    {
        public readonly Vt100Emulator _emulator;
        private TerminalEmulator? _terminalEmulator;
        private readonly ObservableCollection<CommandLogEntry> _commandLog = new();
        private const int MAX_LOG_ENTRIES = 1000;
        private System.Windows.Threading.DispatcherTimer? _updateTimer;

        public DebugPanel(Vt100Emulator emulator, TerminalEmulator? terminalEmulator = null)
        {
            InitializeComponent();
            _emulator = emulator;
            _terminalEmulator = terminalEmulator;
            CommandLogList.ItemsSource = _commandLog;
            
            UpdateModeCheckboxes();
            UpdateCursorInfo();
            UpdateScrollRegionInfo();
            
            _emulator.DebugCommandExecuted += (s, e) => AddCommandLog(e);
            
            // Update info periodically
            _updateTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _updateTimer.Tick += (s, e) =>
            {
                UpdateModeCheckboxes();
                UpdateCursorInfo();
                UpdateScrollRegionInfo();
                UpdatePerformanceMetrics();
                UpdateRenderMetrics();
            };
            _updateTimer.Start();
        }

    public void AddCommandLog(Vt100Emulator.DebugCommandEventArgs e)
    {
        if (Dispatcher.CheckAccess())
        {
            AddCommandLogEntry(e);
        }
        else
        {
            Dispatcher.BeginInvoke(new Action(() => AddCommandLogEntry(e)));
        }
    }
    
    private void AddCommandLogEntry(Vt100Emulator.DebugCommandEventArgs e)
    {
        string displayText;
        
        if (e.CommandType == "Text")
        {
            // For text, show it more simply
            var rawTextEscaped = TabbySSH.Utils.AnsiParser.EscapeString(e.RawText);
            var cursorBefore = e.CursorRowBefore >= 0 ? $"({e.CursorRowBefore},{e.CursorColBefore})" : "(-,-)";
            var cursorAfter = e.CursorRowAfter >= 0 ? $"({e.CursorRowAfter},{e.CursorColAfter})" : "(-,-)";
            displayText = $"[Text] \"{rawTextEscaped}\"\n  Cursor: {cursorBefore} -> {cursorAfter}";
        }
        else
        {
            // For commands, show full details
            var rawTextEscaped = TabbySSH.Utils.AnsiParser.EscapeString(e.RawText);
            var cursorBefore = e.CursorRowBefore >= 0 ? $"({e.CursorRowBefore},{e.CursorColBefore})" : "(-,-)";
            var cursorAfter = e.CursorRowAfter >= 0 ? $"({e.CursorRowAfter},{e.CursorColAfter})" : "(-,-)";
            
            displayText = $"[{e.CommandType}] Raw: {rawTextEscaped}\n  Interpretation: {e.CommandInterpretation}\n  Cursor: {cursorBefore} -> {cursorAfter}\n  Result: {e.ResultingState}";
        }
        
        var entry = new CommandLogEntry
        {
            Timestamp = e.Timestamp.ToString("HH:mm:ss.fff"),
            DisplayText = displayText
        };
        
        _commandLog.Insert(0, entry);
        
        if (_commandLog.Count > MAX_LOG_ENTRIES)
        {
            _commandLog.RemoveAt(_commandLog.Count - 1);
        }
        
        UpdateModeCheckboxes();
        UpdateCursorInfo();
        UpdateScrollRegionInfo();
    }

    private void UpdateModeCheckboxes()
    {
        if (_emulator == null) return;
        
        var modes = _emulator.Modes;
        ModeInAlternateScreen.IsChecked = modes.InAlternateScreen;
        ModeCursorKeyMode.IsChecked = modes.CursorKeyMode;
        ModeInsertMode.IsChecked = modes.InsertMode;
        ModeAutoWrapMode.IsChecked = modes.AutoWrapMode;
        ModeCursorVisible.IsChecked = modes.CursorVisible;
        ModeBracketedPasteMode.IsChecked = modes.BracketedPasteMode;
    }

    private void UpdateCursorInfo()
    {
        if (_emulator == null) return;
        
        CursorInfoText.Text = $"Row: {_emulator.CursorRow}, Col: {_emulator.CursorCol}";
        BufferInfoText.Text = $"Buffer: {(_emulator.InAlternateScreen ? "alternate" : "main")}";
        BufferSizeText.Text = $"Buffer Size: {_emulator.LineCount} lines";
    }

    private void UpdateScrollRegionInfo()
    {
        if (_emulator == null) return;
        
        var modes = _emulator.Modes;
        if (modes.IsScrollRegionActive)
        {
            ScrollRegionText.Text = $"Active: lines {modes.ScrollRegionTop + 1}-{modes.ScrollRegionBottom + 1} (0-based: {modes.ScrollRegionTop}-{modes.ScrollRegionBottom})";
            ScrollRegionTopInput.Text = (modes.ScrollRegionTop + 1).ToString();
            ScrollRegionBottomInput.Text = (modes.ScrollRegionBottom + 1).ToString();
        }
        else
        {
            ScrollRegionText.Text = "Not active";
            ScrollRegionTopInput.Text = "";
            ScrollRegionBottomInput.Text = "";
        }
    }

    private void ModeCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_emulator == null || sender is not CheckBox checkBox) return;
        
        string modeName = checkBox.Name switch
        {
            "ModeInAlternateScreen" => "InAlternateScreen",
            "ModeCursorKeyMode" => "CursorKeyMode",
            "ModeInsertMode" => "InsertMode",
            "ModeAutoWrapMode" => "AutoWrapMode",
            "ModeCursorVisible" => "CursorVisible",
            "ModeBracketedPasteMode" => "BracketedPasteMode",
            _ => ""
        };
        
        if (!string.IsNullOrEmpty(modeName))
        {
            _emulator.SetMode(modeName, checkBox.IsChecked == true);
        }
    }

    private void ScrollRegionInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Update when user types
    }

    private void SetScrollRegion_Click(object sender, RoutedEventArgs e)
    {
        if (_emulator == null) return;
        
        if (int.TryParse(ScrollRegionTopInput.Text, out int top) && 
            int.TryParse(ScrollRegionBottomInput.Text, out int bottom))
        {
            // Convert to 1-based for ANSI command
            string command = $"\x1B[{top};{bottom}r";
            _emulator.SendAnsiCode(command);
        }
    }

    private void SendAnsiCode_Click(object sender, RoutedEventArgs e)
    {
        SendAnsiCode();
    }

    private void AnsiCodeInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            SendAnsiCode();
            e.Handled = true;
        }
    }

    private void SendAnsiCode()
    {
        if (_emulator == null || string.IsNullOrEmpty(AnsiCodeInput.Text)) return;
        
        string code = AnsiCodeInput.Text;
        _emulator.SendAnsiCode(code);
        AnsiCodeInput.Text = "";
    }

    private void AnsiCodeExamples_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox comboBox && comboBox.SelectedItem is ComboBoxItem item && item.Tag is string code)
        {
            AnsiCodeInput.Text = code;
            comboBox.SelectedIndex = 0; // Reset to "-- Examples --"
        }
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        _commandLog.Clear();
    }
    
        private void UpdatePerformanceMetrics()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== EMULATION METRICS ===");
            var metrics = _emulator.GetPerformanceMetrics();
            if (metrics.Count == 0)
            {
                sb.AppendLine("No metrics collected yet");
            }
            else
            {
                sb.AppendLine("Operation              | Avg (ms)  | Total (ms) | Calls      | Min (ms)  | Max (ms)");
                sb.AppendLine("-----------------------|-----------|------------|------------|-----------|----------");
                
                foreach (var kvp in metrics.OrderByDescending(m => m.Value.totalMs))
                {
                    var (avgMs, totalMs, calls, minMs, maxMs) = kvp.Value;
                    sb.AppendLine($"{kvp.Key,-22} | {avgMs,9:F4} | {totalMs,10:F2} | {calls,10} | {minMs,9:F4} | {maxMs,8:F4}");
                }
            }
            
            if (_terminalEmulator != null)
            {
                sb.AppendLine();
                sb.AppendLine("=== RENDERING METRICS ===");
                var renderMetrics = _terminalEmulator.GetRenderMetrics();
                if (renderMetrics.Count == 0)
                {
                    sb.AppendLine("No render metrics collected yet");
                }
                else
                {
                    sb.AppendLine("Operation              | Avg (ms)  | Total (ms) | Calls      | Min (ms)  | Max (ms)");
                    sb.AppendLine("-----------------------|-----------|------------|------------|-----------|----------");
                    
                    foreach (var kvp in renderMetrics.OrderByDescending(m => m.Value.totalMs))
                    {
                        var (avgMs, totalMs, calls, minMs, maxMs) = kvp.Value;
                        sb.AppendLine($"{kvp.Key,-22} | {avgMs,9:F4} | {totalMs,10:F2} | {calls,10} | {minMs,9:F4} | {maxMs,8:F4}");
                    }
                }
            }
            
            PerformanceMetricsText.Text = sb.ToString();
        }
        
        private void UpdateRenderMetrics()
        {
            // This is called from the timer, but UpdatePerformanceMetrics already includes render metrics
        }
    
    private void ResetMetrics_Click(object sender, RoutedEventArgs e)
    {
        _emulator.ResetPerformanceMetrics();
        if (_terminalEmulator != null)
        {
            _terminalEmulator.ResetRenderMetrics();
        }
        UpdatePerformanceMetrics();
    }

    private class CommandLogEntry
    {
        public string Timestamp { get; set; } = string.Empty;
        public string DisplayText { get; set; } = string.Empty;
    }
}
#endif
