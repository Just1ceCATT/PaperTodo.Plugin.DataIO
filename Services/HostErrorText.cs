using PaperTodo.Plugin;

namespace PaperTodo.Plugin.DataIo;

/// <summary>
/// 把 PaperTodo 宿主抛出的错误码转换成面向用户的中文提示。
/// 宿主返回的 Message 多为英文（例如 “PaperTodo cannot create another paper.”），
/// 直接透传会让普通用户看不懂，也无法知道下一步该做什么。
/// </summary>
internal static class HostErrorText
{
    /// <summary>常见错误码的中文说明；未知错误返回 null。</summary>
    private static string? Explain(string code) => code switch
    {
        "permission_denied" => "缺少插件权限，请在 PaperTodo 设置中重新授权后重启。",
        "paper_limit" => "当前工作区的纸片数量已达上限（200 张）。请先清理纸片后再导入。",
        "paper_not_found" => "目标纸片已不存在，可能已被删除；请重新导入文件后确认。",
        "session_closed" => "纸片会话已失效，请重新打开 DataIO 纸片后再试。",
        "data_reload_in_progress" => "PaperTodo 正在重载数据，请稍后重试。",
        "app_exiting" => "PaperTodo 正在退出，操作已取消。",
        "target_changed" => "更新目标在确认后被删除或类型改变。",
        "paper_links_disabled" => "PaperTodo 未开启“待办关联纸片”功能。",
        "reminders_disabled" => "PaperTodo 未开启提醒功能。",
        "invalid_params" => "PaperTodo 拒绝了本次参数（可能超出标题或内容限制）。",
        "save_failed" => "保存数据失败，请检查磁盘空间或文件是否被占用。",
        _ => null
    };

    /// <summary>中文说明 + 原始错误码与消息，便于用户反馈与排查。</summary>
    public static string Describe(PaperTodoPluginException ex)
    {
        var explanation = Explain(ex.Code);
        var detail = string.IsNullOrWhiteSpace(ex.Message) ? ex.Code : $"{ex.Code}：{ex.Message}";
        return explanation is null ? detail : $"{explanation}（{detail}）";
    }

    /// <summary>只给出中文说明；未知错误码回退到原始消息。</summary>
    public static string Describe(string code, string? message)
    {
        var explanation = Explain(code);
        if (explanation is not null)
        {
            return explanation;
        }

        return string.IsNullOrWhiteSpace(message) ? code : message;
    }
}
