param(
    # 宿主仓库根目录，构建脚本会安装到 <PaperTodoRoot>\plugins\<插件 id>\
    [string]$PaperTodoRoot = "Z:\tool\PaperTodo-dev"
)

$ErrorActionPreference = "Stop"

# 复用宿主提供的共享构建/安装脚本，保证产物清理和部署规则与官方一致。
$sharedScript = Join-Path $PaperTodoRoot "plugin-samples\Build-And-Install-NativePlugin.ps1"
if (-not (Test-Path -LiteralPath $sharedScript -PathType Leaf)) {
    throw "未找到宿主构建脚本：$sharedScript"
}

$projectPath = Join-Path $PSScriptRoot "..\PaperTodo.Plugin.DATAIO.csproj"

& $sharedScript -ProjectPath $projectPath -PaperTodoRoot $PaperTodoRoot
