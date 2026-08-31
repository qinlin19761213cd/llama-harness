namespace LlamaHarness;

/// <summary>Markdown → RichTextBox 渲染工具（静态无状态）。支持标题/代码块/列表/引用/粗体/行内代码。</summary>
public static class MarkdownRenderer
{
    /// <summary>将 Markdown 文档渲染到 RichTextBox（支持标题/代码块/列表/粗体/行内代码）。</summary>
    public static void RenderToRichTextBox(RichTextBox rtb, string md)
    {
        rtb.Clear();
        rtb.ReadOnly = true;
        rtb.BackColor = UiTheme.C_TextBg;
        rtb.ForeColor = UiTheme.C_TextFg;
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
                rtb.SelectionColor = UiTheme.C_TextFg;
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
                rtb.SelectionColor = UiTheme.C_TextFg;
                rtb.AppendText("\n");
                continue;
            }

            // 普通段落
            rtb.SelectionStart = rtb.TextLength;
            rtb.SelectionFont = new Font("Microsoft YaHei UI", 9F);
            rtb.SelectionColor = UiTheme.C_TextFg;
            rtb.AppendText(StripMdInline(line) + "\n");
        }

        // 重置默认字体
        rtb.SelectionStart = 0;
        rtb.SelectionLength = 0;
        rtb.SelectionFont = new Font("Microsoft YaHei UI", 9F);
        rtb.SelectionColor = UiTheme.C_TextFg;
    }

    /// <summary>去除行内 Markdown 标记（**粗体** / `code` / [text](url)）。</summary>
    public static string StripMdInline(string text)
    {
        var s = System.Text.RegularExpressions.Regex.Replace(text, @"\*\*(.+?)\*\*", "$1");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"`(.+?)`", "$1");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\[(.+?)\]\(.*?\)", "$1");
        return s;
    }
}
