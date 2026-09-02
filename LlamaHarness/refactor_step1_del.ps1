# 步骤1：从 MainForm.cs 删除已迁出的成员（UiTheme / MarkdownRenderer）
$ErrorActionPreference = 'Stop'
$p = 'C:\project\lunch\LlamaHarness\MainForm.cs'
$c = [System.IO.File]::ReadAllText($p)
$origLen = $c.Length

# 归一化为 CRLF 的辅助函数（here-string 是 LF，文件是 CRLF）
function To-CrLf([string]$s) { $s -replace "`r?`n", "`r`n" }

# 每一项: @{ old = 要删除的原文; name = 描述 }
$blocks = @()

$blocks += @{ name = '颜色常量块'; old = @'
    // ════════════ 全局配色（对齐 Auto_Pilot 参考界面：深色底 + 橙黄强调）════════════
    private static readonly Color C_Bg = Color.FromArgb(0x1A, 0x1A, 0x1A);        // #1a1a1a 页面背景
    private static readonly Color C_Card = Color.FromArgb(0x2D, 0x2D, 0x2D);      // #2d2d2d 侧边栏/卡片/按钮底
    private static readonly Color C_Frame = Color.FromArgb(0x21, 0x21, 0x21);     // #212121 框架/状态面板底
    private static readonly Color C_TextBg = Color.FromArgb(0x1E, 0x1E, 0x1E);    // #1e1e1e 文本区/网格底
    private static readonly Color C_TextFg = Color.FromArgb(0xE0, 0xE0, 0xE0);    // #e0e0e0 正文文字
    private static readonly Color C_Btn = Color.FromArgb(0x3D, 0x3D, 0x3D);       // #3d3d3d 按钮底
    private static readonly Color C_BtnHover = Color.FromArgb(0x4A, 0x4A, 0x4A);  // #4a4a4a 按钮悬停
    private static readonly Color C_Primary = Color.FromArgb(0xFF, 0xA5, 0x00);   // #FFA500 橙黄强调（大标题/选中页签）
    private static readonly Color C_Title = Color.FromArgb(0xE0, 0xE0, 0xE0);     // #e0e0e0 一级标题
    private static readonly Color C_Aux = Color.FromArgb(0x86, 0x90, 0x9C);       // #86909C 辅助说明
    private static readonly Color C_Green = Color.FromArgb(0x27, 0xAE, 0x60);     // #27AE60 运行中
    private static readonly Color C_Red = Color.FromArgb(0xE7, 0x4C, 0x3C);       // #E74C3C 已停止/异常
    private static readonly Color C_Warn = Color.FromArgb(0xFF, 0x98, 0x00);      // #FF9800 过渡态（唤醒/休眠）

'@ }

$blocks += @{ name = 'IconCache 字段'; old = @'
    // —— 图标缓存（static/icon/*.png，缺失时降级纯文本按钮）——
    private static readonly Dictionary<string, Image> IconCache = new(StringComparer.OrdinalIgnoreCase);
'@ }

$blocks += @{ name = '卡片工厂（MakeCardPanel/MakeRawButton/MakeRawTextBox）'; old = @'
    /// <summary>创建卡片容器 Panel（深色底 + AutoSize，高度随内容自增长）。</summary>
    private static Panel MakeCardPanel() => new()
    {
        Dock = DockStyle.Fill,
        BackColor = C_Card,
        Padding = new Padding(8),
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
    };

    /// <summary>创建 [查看原始报文 ▸] 按钮（Dock Bottom，位于数据区下方）。</summary>
    private static Button MakeRawButton() => new()
    {
        Text = "查看原始报文 ▸",
        Dock = DockStyle.Bottom,
        Height = 22,
        FlatStyle = FlatStyle.Flat,
        BackColor = Color.FromArgb(0x3D, 0x3D, 0x3D),
        ForeColor = Color.FromArgb(0xAA, 0xAA, 0xAA),
        Font = new Font("Microsoft YaHei UI", 8F),
        TextAlign = ContentAlignment.MiddleLeft,
        Cursor = Cursors.Hand,
        Visible = false,
    };

    /// <summary>创建 Raw 内容 TextBox（Dock Bottom，等宽字体 + 只读 + 可滚动，高度 200px）。</summary>
    private static TextBox MakeRawTextBox() => new()
    {
        Dock = DockStyle.Bottom,
        Height = 200,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        WordWrap = false,
        BackColor = C_TextBg,
        ForeColor = Color.FromArgb(0x99, 0xCC, 0x99),
        Font = new Font("Consolas", 8F),
        BorderStyle = BorderStyle.FixedSingle,
        Visible = false,
    };

'@ }

$blocks += @{ name = 'LoadIcon'; old = @'
    /// <summary>从 static/icon 加载图标并缩放到 16x16（缓存；文件缺失返回 null → 按钮降级纯文本）。
    /// 参考图标原始尺寸 300px+，必须缩放，否则挤占按钮文字区域。
    /// 黑色图标自动反转为白色（统一深色主题下的图标颜色）。</summary>
    private static Image? LoadIcon(string fileName)
    {
        if (IconCache.TryGetValue(fileName, out var cached)) return cached;
        var path = Path.Combine(AppContext.BaseDirectory, "static", "icon", fileName);
        try
        {
            if (File.Exists(path))
            {
                using var src = new Bitmap(path); // 构造时同步读入内存，不持有文件句柄
                var img = new Bitmap(16, 16);     // 缩放到 16x16（侧边栏按钮图标标准尺寸）
                using (var g = Graphics.FromImage(img))
                    g.DrawImage(src, 0, 0, 16, 16);

                // 黑色图标 → 白色：检测非透明像素平均亮度，偏黑（<128）才反转，白底黑图保持原样
                int totalA = 0, totalLum = 0;
                for (int y = 0; y < 16; y++)
                    for (int x = 0; x < 16; x++)
                    {
                        var p = img.GetPixel(x, y);
                        if (p.A > 0)
                        {
                            totalA++;
                            totalLum += (p.R + p.G + p.B) / 3; // 简单亮度估算
                        }
                    }
                if (totalA > 0 && totalLum / totalA < 128) // 平均亮度偏黑 → 反转
                {
                    for (int y = 0; y < 16; y++)
                        for (int x = 0; x < 16; x++)
                        {
                            var p = img.GetPixel(x, y);
                            if (p.A > 0)
                                img.SetPixel(x, y, Color.FromArgb(p.A, 255 - p.R, 255 - p.G, 255 - p.B));
                        }
                }

                IconCache[fileName] = img;
                return img;
            }
        }
        catch
        {
            // 图标损坏/不可读：降级纯文本按钮
        }
        return null;
    }

'@ }

$blocks += @{ name = 'Markdown 渲染（RenderMarkdownToRichTextBox + StripMdInline）'; old = @'
    /// <summary>帮助文档窗体（只读深色 TextBox 显示 static/doc 下对应 md；文件缺失时提示）。</summary>
    /// <summary>将 Markdown 文档渲染到 RichTextBox（支持标题/代码块/列表/粗体/行内代码）。</summary>
    private static void RenderMarkdownToRichTextBox(RichTextBox rtb, string md)
    {
        rtb.Clear();
        rtb.ReadOnly = true;
        rtb.BackColor = C_TextBg;
        rtb.ForeColor = C_TextFg;
        rtb.Font = new Font("Microsoft YaHei UI", 9F);

        var lines = md.Split('\n');
        bool inCodeBlock = false;

        foreach (var line in lines)
        {
            // 代码块开关
            if (line.StartsWith("```"))
            {
                inCodeBlock = !inCodeBlock;
                continue;
            }

            if (inCodeBlock)
            {
                rtb.SelectionStart = rtb.TextLength;
                rtb.SelectionFont = new Font("Consolas", 9F);
                rtb.SelectionColor = Color.FromArgb(0x99, 0xCC, 0x99);
                rtb.AppendText(line + "\n");
                continue;
            }

            // 标题
            if (line.StartsWith("#### "))
            {
                rtb.SelectionStart = rtb.TextLength;
                rtb.SelectionFont = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
                rtb.SelectionColor = Color.FromArgb(0xFF, 0xA5, 0x00);
                rtb.AppendText(line.Substring(5) + "\n\n");
                continue;
            }
            if (line.StartsWith("### "))
            {
                rtb.SelectionStart = rtb.TextLength;
                rtb.SelectionFont = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold);
                rtb.SelectionColor = Color.FromArgb(0xFF, 0xA5, 0x00);
                rtb.AppendText(line.Substring(4) + "\n\n");
                continue;
            }
            if (line.StartsWith("## "))
            {
                rtb.SelectionStart = rtb.TextLength;
                rtb.SelectionFont = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold);
                rtb.SelectionColor = Color.FromArgb(0xFF, 0xA5, 0x00);
                rtb.AppendText(line.Substring(3) + "\n\n");
                continue;
            }
            if (line.StartsWith("# "))
            {
                rtb.SelectionStart = rtb.TextLength;
                rtb.SelectionFont = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold);
                rtb.SelectionColor = Color.FromArgb(0xFF, 0xA5, 0x00);
                rtb.AppendText(line.Substring(2) + "\n\n");
                continue;
            }

            // 列表项
            if (line.StartsWith("- ") || line.StartsWith("* "))
            {
                rtb.SelectionStart = rtb.TextLength;
                rtb.SelectionFont = new Font("Microsoft YaHei UI", 9F);
                rtb.SelectionColor = C_TextFg;
                rtb.AppendText("  • " + StripMdInline(line.Substring(2)) + "\n");
                continue;
            }

            // 引用块
            if (line.StartsWith("> "))
            {
                rtb.SelectionStart = rtb.TextLength;
                rtb.SelectionFont = new Font("Microsoft YaHei UI", 9F, FontStyle.Italic);
                rtb.SelectionColor = Color.FromArgb(0x88, 0x88, 0x88);
                rtb.AppendText("  │ " + StripMdInline(line.Substring(2)) + "\n");
                continue;
            }

            // 空行
            if (string.IsNullOrWhiteSpace(line))
            {
                rtb.SelectionStart = rtb.TextLength;
                rtb.SelectionFont = new Font("Microsoft YaHei UI", 9F);
                rtb.SelectionColor = C_TextFg;
                rtb.AppendText("\n");
                continue;
            }

            // 普通段落
            rtb.SelectionStart = rtb.TextLength;
            rtb.SelectionFont = new Font("Microsoft YaHei UI", 9F);
            rtb.SelectionColor = C_TextFg;
            rtb.AppendText(StripMdInline(line) + "\n");
        }

        // 重置默认字体
        rtb.SelectionStart = 0;
        rtb.SelectionLength = 0;
        rtb.SelectionFont = new Font("Microsoft YaHei UI", 9F);
        rtb.SelectionColor = C_TextFg;
    }

    /// <summary>去除行内 Markdown 标记（**粗体** / `code` / [text](url)）。</summary>
    private static string StripMdInline(string text)
    {
        var s = System.Text.RegularExpressions.Regex.Replace(text, @"\*\*(.+?)\*\*", "$1");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"`(.+?)`", "$1");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\[(.+?)\]\(.*?\)", "$1");
        return s;
    }

'@ }

$blocks += @{ name = 'MakeCardTitle + MakeTabBtn'; old = @'
    /// <summary>卡片标题行（加粗 + 固定高度，用于系统资源页各区块标题）。</summary>
    private static Label MakeCardTitle(string text) => new()
    {
        Text = $"  {text}",
        Dock = DockStyle.Top,
        AutoSize = true,
        ForeColor = C_Title,
        BackColor = Color.Black,
        Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(0, 4, 0, 4),
    };

    /// <summary>扁平页签按钮（#3d3d3d 底白字，尺寸自适应文字，无边框）。</summary>
    private static Button MakeTabBtn(string text)
    {
        var b = new Button
        {
            Text = text,
            AutoSize = true,
            Padding = new Padding(12, 4, 12, 4),
            FlatStyle = FlatStyle.Flat,
            BackColor = C_Btn,
            ForeColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 9F),
        };
        b.FlatAppearance.BorderSize = 0; // 无边框，消除白边
        return b;
    }

'@ }

$blocks += @{ name = 'MakeBtn'; old = @'
    /// <summary>创建统一风格侧边栏按钮（#3d3d3d 底白字 + 左侧图标，悬停变亮；图标缺失降级纯文本）。</summary>
    private static Button MakeBtn(string text, string? iconFile = null, bool enabled = true, int h = 34)
    {
        var b = new Button
        {
            Text = text,
            Size = new Size(168, h),
            FlatStyle = FlatStyle.Flat,
            BackColor = C_Btn,
            ForeColor = Color.White, // 统一白字（禁用态也保持白色，清晰）
            Enabled = enabled,
            Font = new Font("Microsoft YaHei UI", 9F),
            TextAlign = ContentAlignment.MiddleCenter,
            ImageAlign = ContentAlignment.MiddleCenter,
            TextImageRelation = TextImageRelation.ImageBeforeText, // 图标+文字整体居中
        };
        b.FlatAppearance.BorderSize = 0; // 无边框，消除白边
        var img = LoadIcon(iconFile ?? "");
        if (img != null) b.Image = img;
        b.MouseEnter += (_, _) => { if (b.Enabled) b.BackColor = C_BtnHover; };
        b.MouseLeave += (_, _) => { if (b.Enabled) b.BackColor = C_Btn; };
        return b;
    }

'@ }

$blocks += @{ name = '表格工厂（MakeGrid/ApplyStatsGridStyle/MakeGridCol/MakeCheckCol/MakeSectionTitle）'; old = @'
    /// <summary>创建统一风格 DataGridView（#1e1e1e 底 / #2d2d2d 网格线，无边框）。</summary>
    private static DataGridView MakeGrid() => new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        BorderStyle = BorderStyle.None, // 无边框，消除白边
        BackgroundColor = C_TextBg,
        ForeColor = C_TextFg,
        GridColor = C_Card,
        RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
    };

    /// <summary>应用统计页表格样式（行高/交替行色/列头样式），保持各页面表格统一。</summary>
    private static void ApplyStatsGridStyle(DataGridView grid)
    {
        grid.DefaultCellStyle.BackColor = C_TextBg;
        grid.DefaultCellStyle.ForeColor = C_TextFg;
        grid.AlternatingRowsDefaultCellStyle.BackColor = C_Frame;
        grid.ColumnHeadersDefaultCellStyle.BackColor = C_Card;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = C_Aux;
        grid.RowTemplate.Height = 22;
    }

    private static DataGridViewTextBoxColumn MakeGridCol(string header) => new()
    {
        HeaderText = header,
        SortMode = DataGridViewColumnSortMode.NotSortable,
    };

    /// <summary>可编辑 CheckBox 列（槽位管理页：强占/KV缓存开关）。</summary>
    private static DataGridViewCheckBoxColumn MakeCheckCol(string header) => new()
    {
        HeaderText = header,
        SortMode = DataGridViewColumnSortMode.NotSortable,
        AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
    };

    /// <summary>左侧分组标题（small bold，黑底容器——Control Panel / Configuration / User Manual；宽度与按键一致，由侧边栏网格中列控制）。</summary>
    private static Label MakeSectionTitle(string text) => new()
    {
        Text = $"  {text}",
        Height = 30, // 固定高度（Dock=Top 时由 AddRow 统一设置 Dock）
        AutoSize = false,
        ForeColor = C_Title,
        BackColor = Color.Black, // 黑底容器（层次感）
        Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(0, 4, 0, 4), // 上下内边距，形成容器包裹感
    };

'@ }

$blocks += @{ name = 'ApplyBlackCheck'; old = @'
    /// <summary>CheckBox 样式：ForeColor=黑（标签文字清晰）+ FlatStyle.Flat + BackColor=白（勾选框底色白，勾默认黑）。禁用时灰。</summary>
    private void ApplyBlackCheck(Control c)
    {
        if (c is CheckBox cb)
        {
            cb.ForeColor = Color.Black; // 标签文字黑色（清晰）
            cb.FlatStyle = FlatStyle.Flat; // 扁平风格
            cb.BackColor = Color.White; // 勾选框底色白（勾默认黑，明显）
            // 禁用时统一灰（ApplyPhase 中处理 Enabled 切换时同步刷新）
            cb.CheckedChanged += (_, _) =>
            {
                var color = cb.Enabled ? Color.Black : Color.FromArgb(0x88, 0x88, 0x88);
                cb.ForeColor = color;
            };
        }
    }

'@ }

$fail = 0
foreach ($b in $blocks) {
    $old = To-CrLf $b.old
    if ($c.Contains($old)) {
        $c = $c.Replace($old, '')
        Write-Host "[OK] 删除: $($b.name)"
    } else {
        Write-Host "[FAIL] 未匹配: $($b.name)"
        $fail++
    }
}

if ($fail -gt 0) {
    Write-Host "存在未匹配块，中止写回"
    exit 1
}

[System.IO.File]::WriteAllText($p, $c, [System.Text.UTF8Encoding]::new($false))
Write-Host "原长度: $origLen -> 新长度: $($c.Length) (减少 $($origLen - $c.Length) 字符)"
