# PaperTodo 数据导入导出插件（DATAIO）

一个 PaperTodo 原生插件：在一张工具纸片里勾选已有的纸片，把它们导出为 JSON；也可以把导出的 JSON 追加导入为新的纸片。

## 能力边界

- 只使用宿主公开的逻辑数据 API（`IPaperTodoHostApi`），不直接读写 `data.json`。
- 导出内容：纸片标题、类型（待办 / 笔记）、可见性、待办列表（文本、完成状态、顺序、提醒、关联纸片）、内置 Markdown 笔记正文。
- 导入策略：一律**追加为新纸片**，宿主生成新 ID，不覆盖、不删除任何现有纸片。
- 无法还原：窗口位置/大小、折叠与置顶状态、待办关联的文件或文件夹、其他插件提供的自绘正文。
- 笔记内嵌图片只保留正文中的 `i:` 引用，不导出图片本体；跨设备导入或原图被清理后无法显示。
- 导入的纸片会归入宿主内置的待办 / Markdown 笔记正文，无法恢复为第三方插件正文。

## 预览

- 每行右侧的预览按钮或双击行会打开预览窗口：标题、类型、字数/待办统计、可滚动正文。
- 预览窗口支持上一条/下一条（← / →）、Esc 关闭、复制正文，并可直接加入/移出导出（导入）选择。
- 鼠标在某一行停留约 0.45 秒会弹出只读快速预览卡片；鼠标移到复选框或按钮上时不弹出。
- 笔记正文按轻量 Markdown 排版展示（标题、列表、引用、代码块与常见行内样式），
  不追求完整规范；正文超过 20 万字符时只显示前一部分。
- 预览内容按需读取并缓存，列表或导入文件变化时自动失效；第三方插件纸片会提示正文不可读取。

## 导出格式

默认生成 **PaperTodo 归档（.zip）**，压缩包内固定包含 `manifest.json`，内容如下。
保存时选择 `.json` 扩展名可直接生成纯 JSON；导入同时支持 `.zip` 和 `.json`。

```json
{
  "format": "papertodo.paper-export",
  "schemaVersion": 1,
  "exportedAt": "2026-11-21T14:30:00+08:00",
  "source": {
    "pluginId": "papertodo.dataio.native",
    "pluginVersion": "1.0.0",
    "apiVersion": "2.3",
    "uiLanguage": "zh-CN",
    "note": "sourceId 仅供溯源与同文件内链接还原；导入时宿主生成新 ID。"
  },
  "papers": [
    {
      "sourceId": "45f84292f605429db24dabc25175bee8",
      "type": "todo",
      "title": "周计划",
      "show": true,
      "todos": [
        { "text": "写周报", "done": false, "order": 0 }
      ]
    },
    {
      "sourceId": "c103868b43e147fc94ad56fbbbcabfa4",
      "type": "note",
      "title": "资料",
      "show": false,
      "contentAvailable": true,
      "content": "# 参考资料"
    }
  ]
}
```

`sourceId` 只用于溯源与**同一文件内**的关联纸片还原，导入时不会作为宿主 ID 使用。

## 权限

| 权限 | 用途 |
| --- | --- |
| `papers.read` | 读取纸片列表 |
| `todos.read` | 导出待办 |
| `notes.read` | 导出笔记正文 |
| `papers.create` | 导入时创建新纸片 |
| `todos.append` | 导入带待办的纸片 |
| `notes.append` | 导入带正文的笔记 |
| `todos.update` | 还原待办顺序、链接与提醒 |

## 构建与安装

需要 .NET 10 SDK 和已构建的宿主仓库 `Z:\tool\PaperTodo-dev`。

```powershell
# 退出 PaperTodo 后执行
.\scripts\Build-And-Install.ps1
```

脚本复用宿主共享脚本，发布为 `win-x64`、框架依赖模式，并安装到：

```text
Z:\tool\PaperTodo-dev\plugins\papertodo.dataio.native\
```

如果宿主是从发布目录运行的（例如 `输出\PaperTodo-v4.0.0-beta2\PaperTodo.exe`），
需要把插件同步到该目录的 `plugins` 根，用 `-AdditionalPluginsRoot` 自动完成：

```powershell
.\scripts\Build-And-Install.ps1 `
  -AdditionalPluginsRoot "Z:\tool\PaperTodo-dev\输出\PaperTodo-v4.0.0-beta2\plugins"
```

安装后重启 PaperTodo。原生插件不支持热重载。

## 设置项

插件设置由宿主绘制：是否显示待办/笔记纸片、导入后是否显示新纸片、导入前确认、是否还原顺序/关联纸片/提醒、启动后是否自动打开工具纸片。

## 已知限制

- 宿主单工作区最多 200 张纸片，导入前会预检。
- 所有 `CreatePaper` 与 `UpdateTodo` 调用都在宿主 UI 线程执行，导入期间界面可能短暂停顿，但每张纸片后会主动让出线程。
- 若宿主开启“已完成待办自动置底/自动清除”，导入后的顺序可能被宿主策略调整。
- 导入是逐张纸片提交的部分成功语义：失败不会回滚已创建的纸片。
- 归档不包含笔记图片本体。导出时会提示含图片的笔记数量；导入到其他设备、或原图片已被清理后，图片引用会失效。极端情况下图片编号被复用，还可能显示成其他图片。
