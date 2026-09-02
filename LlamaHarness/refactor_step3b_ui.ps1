# 步骤3b：MainForm.Ui.cs —— 删除日志区字段（迁至 LogView），_txtLog 改用 _logView.TxtLog
$ErrorActionPreference = 'Stop'
$p = 'C:\project\lunch\LlamaHarness\MainForm.Ui.cs'
$c = [System.IO.File]::ReadAllText($p)
function To-CrLf([string]$s) { $s -replace "`r?`n", "`r`n" }
$fail = 0

# 1. 删除日志区字段块（_txtLog / _logQueue / _logFlushTimer → LogView）
$old = @'
    // —— 日志区（RichTextBox：按行独立着色 + 防抖）——
    private readonly RichTextBox _txtLog = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = RichTextBoxScrollBars.Vertical,
        WordWrap = false,
        BorderStyle = BorderStyle.None, // 无边框，消除白边
        BackColor = UiTheme.C_TextBg,
        ForeColor = UiTheme.C_TextFg,
        Font = new Font("Consolas", 9F),
    };
    private readonly Queue<(string line, string entry)> _logQueue = new();
    private readonly System.Windows.Forms.Timer _logFlushTimer = new() { Interval = 150 };

'@
$new = @'
    // —— 主日志区（LogView 承载：RichTextBox 按行独立着色 + 防抖）——
'@
$c2 = $c.Replace((To-CrLf $old), (To-CrLf $new))
if ($c2 -eq $c) { Write-Host '[FAIL] 日志区字段块未匹配'; $fail++ } else { Write-Host '[OK] 日志区字段块删除'; $c = $c2 }

# 2. _tabPages 中 _txtLog → _logView.TxtLog
$c2 = $c.Replace((To-CrLf '_tabPages = new Control[] { _txtLog, tabStats, tabSlots, _tabSlotMgmt, tabRes, _tabConfig, _docPanel };'),
                 (To-CrLf '_tabPages = new Control[] { _logView.TxtLog, tabStats, tabSlots, _tabSlotMgmt, tabRes, _tabConfig, _docPanel };'))
if ($c2 -eq $c) { Write-Host '[FAIL] _txtLog 引用未替换'; $fail++ } else { Write-Host '[OK] _txtLog → _logView.TxtLog'; $c = $c2 }

if ($fail -gt 0) { Write-Host '存在未匹配项，中止写回'; exit 1 }
[System.IO.File]::WriteAllText($p, $c, [System.Text.UTF8Encoding]::new($false))
Write-Host 'MainForm.Ui.cs 改造完成'
