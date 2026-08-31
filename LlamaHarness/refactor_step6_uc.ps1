# 步骤6：区域 UserControl 化 —— 4 个 Controller 改为 : UserControl 并改名 View，LogView 同步升级
$ErrorActionPreference = 'Stop'
$base = 'C:\project\lunch\LlamaHarness'
function EditFile([string]$rel, [scriptblock]$fn) {
    $p = Join-Path $base $rel
    $c = [System.IO.File]::ReadAllText($p)
    $c0 = $c
    & $fn ([ref]$c)
    if ($c -ne $c0) {
        [System.IO.File]::WriteAllText($p, $c, [System.Text.UTF8Encoding]::new($false))
        Write-Host "[OK] $rel"
    } else { Write-Host "[SKIP] $rel（无变化）" }
}

# ===== StatusPanelController.cs =====
EditFile 'StatusPanelController.cs' {
    param([ref]$c)
    $c.Value = $c.Value.Replace('public sealed class StatusPanelController', 'public sealed class StatusPanelView : UserControl')
    $c.Value = $c.Value.Replace("var panel = new Panel`r`n        {`r`n            Dock = DockStyle.Fill,`r`n            BackColor = UiTheme.C_Frame,`r`n            Padding = new Padding(12),`r`n            AutoScroll = true,`r`n        };",
        "Dock = DockStyle.Fill;`r`n        BackColor = UiTheme.C_Frame;`r`n        Padding = new Padding(12);`r`n        AutoScroll = true;")
    $c.Value = $c.Value.Replace('panel.Controls.Add(grid);', 'Controls.Add(grid);')
    $c.Value = $c.Value.Replace('        return panel;', '        return this;')
}

# ===== StatsPanelController.cs =====
EditFile 'StatsPanelController.cs' {
    param([ref]$c)
    $c.Value = $c.Value.Replace('public sealed class StatsPanelController', 'public sealed class StatsPanelView : UserControl')
    $c.Value = $c.Value.Replace('    public Control BuildPage()
    {
        var panel = new TableLayoutPanel', "    public Control BuildPage()
    {
        Dock = DockStyle.Fill;
        var panel = new TableLayoutPanel")
    $c.Value = $c.Value.Replace('        return panel;', '        Controls.Add(panel);
        return this;')
}

# ===== SlotPanelController.cs =====
EditFile 'SlotPanelController.cs' {
    param([ref]$c)
    $c.Value = $c.Value.Replace('public sealed class SlotPanelController', 'public sealed class SlotPanelView : UserControl')
    $c.Value = $c.Value.Replace('        var page = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.C_Bg, Padding = new Padding(10) };
        _txtSlotLog = new RichTextBox', '        Dock = DockStyle.Fill;
        BackColor = UiTheme.C_Bg;
        Padding = new Padding(10);
        _txtSlotLog = new RichTextBox')
    $c.Value = $c.Value.Replace('page.Controls.Add(_txtSlotLog);', 'Controls.Add(_txtSlotLog);')
    $c.Value = $c.Value.Replace('page.Controls.Add(_gridSlots);', 'Controls.Add(_gridSlots);')
    $c.Value = $c.Value.Replace('        return page;', '        return this;')
}

# ===== MonitorPanelController.cs =====
EditFile 'MonitorPanelController.cs' {
    param([ref]$c)
    $c.Value = $c.Value.Replace('public sealed class MonitorPanelController', 'public sealed class MonitorPanelView : UserControl')
    $c.Value = $c.Value.Replace('        var page = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.C_Bg, Padding = new Padding(10), AutoScroll = true };', '        Dock = DockStyle.Fill;
        BackColor = UiTheme.C_Bg;
        Padding = new Padding(10);
        AutoScroll = true;')
    $c.Value = $c.Value.Replace('page.Controls.Add(resLayout);', 'Controls.Add(resLayout);')
    $c.Value = $c.Value.Replace('        return page;', '        return this;')
}

# ===== LogView.cs =====
EditFile 'LogView.cs' {
    param([ref]$c)
    $c.Value = $c.Value.Replace('public sealed class LogView', 'public sealed class LogView : UserControl')
    $newCtor = @"
    public LogView()
    {
        Dock = DockStyle.Fill;
        Controls.Add(TxtLog);
    }

"@
    $anchor = '    public bool HasPending { get { lock (_logQueue) return _logQueue.Count > 0; } }'
    $c.Value = $c.Value.Replace($anchor, $anchor + "`r`n" + $newCtor.TrimEnd("`r`n") + "`r`n")
}

# ===== MainForm.cs 类型引用 =====
EditFile 'MainForm.cs' {
    param([ref]$c)
    $c.Value = $c.Value.Replace('StatusPanelController', 'StatusPanelView')
    $c.Value = $c.Value.Replace('StatsPanelController', 'StatsPanelView')
    $c.Value = $c.Value.Replace('SlotPanelController', 'SlotPanelView')
    $c.Value = $c.Value.Replace('MonitorPanelController', 'MonitorPanelView')
}

# ===== MainFormPresenter.cs 类型引用 =====
EditFile 'MainFormPresenter.cs' {
    param([ref]$c)
    $c.Value = $c.Value.Replace('StatusPanelController', 'StatusPanelView')
    $c.Value = $c.Value.Replace('StatsPanelController', 'StatsPanelView')
    $c.Value = $c.Value.Replace('SlotPanelController', 'SlotPanelView')
    $c.Value = $c.Value.Replace('MonitorPanelController', 'MonitorPanelView')
}

# ===== MainForm.Ui.cs：日志页签 _logView.TxtLog → _logView =====
EditFile 'MainForm.Ui.cs' {
    param([ref]$c)
    $c.Value = $c.Value.Replace('_tabPages = new Control[] { _logView.TxtLog, tabStats', '_tabPages = new Control[] { _logView, tabStats')
}

Write-Host '全部内容修改完成'
