namespace DynamicIsland.Core.Model;

/// <summary>
/// TodoWrite 一项待办的归一化数据（纯数据，不引用 WPF，便于日后整体搬进 Core 做单测）。
/// </summary>
/// <param name="Content">待办内容（祈使句）。</param>
/// <param name="ActiveForm">进行中表述（现在分词）；TodoWrite 每项都带，in_progress 时优先显示它。</param>
/// <param name="Status">状态：pending / in_progress / completed。采集层原样保留字符串，UI 层 switch 兜底未知值。</param>
public record TodoItem(string Content, string ActiveForm, string Status);
