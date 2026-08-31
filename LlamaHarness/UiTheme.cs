namespace LlamaHarness;

/// <summary>
/// 全局 UI 主题与控件工厂（深色底 + 橙黄强调，对齐 Auto_Pilot 参考界面）。
/// 纯静态无状态：配色常量 + 统一风格控件创建，供 MainForm 及各 View 组件复用。
/// </summary>
public static class UiTheme
{
    // ════════════ 全局配色（对齐 Auto_Pilot 参考界面：深色底 + 橙黄强调）════════════
    public static readonly Color C_Bg = Color.FromArgb(0x1A, 0x1A, 0x1A);        // #1a1a1a 页面背景
    public static readonly Color C_Card = Color.FromArgb(0x2D, 0x2D, 0x2D);      // #2d2d2d 侧边栏/卡片/按钮底
    public static readonly Color C_Frame = Color.FromArgb(0x21, 0x21, 0x21);     // #212121 框架/状态面板底
    public static readonly Color C_TextBg = Color.FromArgb(0x1E, 0x1E, 0x1E);    // #1e1e1e 文本区/网格底
    public static readonly Color C_TextFg = Color.FromArgb(0xE0, 0xE0, 0xE0);    // #e0e0e0 正文文字
    public static readonly Color C_Btn = Color.FromArgb(0x3D, 0x3D, 0x3D);       // #3d3d3d 按钮底
    public static readonly Color C_BtnHover = Color.FromArgb(0x4A, 0x4A, 0x4A);  // #4a4a4a 按钮悬停
    public static readonly Color C_Primary = Color.FromArgb(0xFF, 0xA5, 0x00);   // #FFA500 橙黄强调（大标题/选中页签）
    public static readonly Color C_Title = Color.FromArgb(0xE0, 0xE0, 0xE0);     // #e0e0e0 一级标题
    public static readonly Color C_Aux = Color.FromArgb(0x86, 0x90, 0x9C);       // #86909C 辅助说明
    public static readonly Color C_Green = Color.FromArgb(0x27, 0xAE, 0x60);     // #27AE60 运行中
    public static readonly Color C_Red = Color.FromArgb(0xE7, 0x4C, 0x3C);       // #E74C3C 已停止/异常
    public static readonly Color C_Warn = Color.FromArgb(0xFF, 0x98, 0x00);      // #FF9800 过渡态（唤醒/休眠）

    // —— 图标缓存（static/icon/*.png，缺失时降级纯文本按钮）——
    private static readonly Dictionary<string, Image> IconCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>从 static/icon 加载图标并缩放到 16x16（缓存；文件缺失返回 null → 按钮降级纯文本）。
    /// 参考图标原始尺寸 300px+，必须缩放，否则挤占按钮文字区域。
    /// 黑色图标自动反转为白色（统一深色主题下的图标颜色）。</summary>
    public static Image? LoadIcon(string fileName)
    {
        if (IconCache.TryGetValue(fileName, out var cached)) return cached;
        var path = AppPaths.IconFile(fileName);
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

    /// <summary>创建统一风格侧边栏按钮（#3d3d3d 底白字 + 左侧图标，悬停变亮；图标缺失降级纯文本）。</summary>
    public static Button MakeBtn(string text, string? iconFile = null, bool enabled = true, int h = 34)
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

    /// <summary>左侧分组标题（small bold，黑底容器——Control Panel / Configuration / User Manual；宽度与按键一致，由侧边栏网格中列控制）。</summary>
    public static Label MakeSectionTitle(string text) => new()
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

    /// <summary>扁平页签按钮（#3d3d3d 底白字，尺寸自适应文字，无边框）。</summary>
    public static Button MakeTabBtn(string text)
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

    /// <summary>创建统一风格 DataGridView（#1e1e1e 底 / #2d2d2d 网格线，无边框）。</summary>
    public static DataGridView MakeGrid() => new()
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
    public static void ApplyStatsGridStyle(DataGridView grid)
    {
        grid.DefaultCellStyle.BackColor = C_TextBg;
        grid.DefaultCellStyle.ForeColor = C_TextFg;
        grid.AlternatingRowsDefaultCellStyle.BackColor = C_Frame;
        grid.ColumnHeadersDefaultCellStyle.BackColor = C_Card;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = C_Aux;
        grid.RowTemplate.Height = 22;
    }

    public static DataGridViewTextBoxColumn MakeGridCol(string header) => new()
    {
        HeaderText = header,
        SortMode = DataGridViewColumnSortMode.NotSortable,
    };

    /// <summary>可编辑 CheckBox 列（槽位管理页：强占/KV缓存开关）。</summary>
    public static DataGridViewCheckBoxColumn MakeCheckCol(string header) => new()
    {
        HeaderText = header,
        SortMode = DataGridViewColumnSortMode.NotSortable,
        AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
    };

    /// <summary>创建卡片容器 Panel（深色底 + AutoSize，高度随内容自增长）。</summary>
    public static Panel MakeCardPanel() => new()
    {
        Dock = DockStyle.Fill,
        BackColor = C_Card,
        Padding = new Padding(8),
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
    };

    /// <summary>卡片标题行（加粗 + 固定高度，用于系统资源页各区块标题）。</summary>
    public static Label MakeCardTitle(string text) => new()
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

    /// <summary>创建 [查看原始报文 ▸] 按钮（Dock Bottom，位于数据区下方）。</summary>
    public static Button MakeRawButton() => new()
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
    public static TextBox MakeRawTextBox() => new()
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

    /// <summary>CheckBox 样式：ForeColor=黑（标签文字清晰）+ FlatStyle.Flat + BackColor=白（勾选框底色白，勾默认黑）。禁用时灰。</summary>
    public static void ApplyBlackCheck(Control c)
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
}
