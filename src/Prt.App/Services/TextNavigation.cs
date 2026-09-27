namespace Prt.App.Services;

/// <summary>
/// 文本位置换算工具。
/// <para>
/// 说明：此处不直接使用 <c>TextBox.GetLineIndexFromCharacterIndex</c>，因为该 API 在开启自动换行时
/// 返回的是「视觉行」而非「逻辑行」，与诊断信息中的 `行:列`（逻辑行）语义不一致。
/// 因此统一按 `\n` 自行换算，保证状态栏、诊断面板与编辑器三者口径一致。
/// </para>
/// </summary>
internal static class TextNavigation
{
    /// <summary>由字符偏移量求逻辑行列（均从 1 起）。</summary>
    public static (int Line, int Column) ToLineColumn(string text, int index)
    {
        index = Math.Clamp(index, 0, text.Length);
        var line = 1;
        var column = 1;
        for (var i = 0; i < index; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                column = 1;
            }
            else
            {
                column++;
            }
        }
        return (line, column);
    }

    /// <summary>由逻辑行列求字符偏移量（越界时钳制到文本范围）。</summary>
    public static int ToIndex(string text, int line, int column)
    {
        if (line <= 1)
        {
            return Math.Clamp(column - 1, 0, text.Length);
        }

        var currentLine = 1;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
            {
                continue;
            }
            currentLine++;
            if (currentLine == line)
            {
                var start = i + 1;
                return Math.Clamp(start + column - 1, 0, text.Length);
            }
        }

        return text.Length;
    }
}
