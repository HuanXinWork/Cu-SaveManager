# 修复已被写坏的多人存档角色字段。
#
# 背景：Together 读档后 body 物种与 charType 不一致时再次保存，会把 save.sv 顶层的
# character 写成 0（Experiment），之后任何恢复逻辑都只能读回 0。/ 本脚本把指定存档的
# character 改回目标角色，并同步 sidecar（save.sv.json）里的 character 字段。
#
# 用法（游戏必须已退出，否则会被再次覆盖）：
#   pwsh -NoProfile -File Fix-MpCharacter.ps1 -Path "<mp_save 目录或单个 save.sv>" -Character 2
#   -Character：0=Experiment 1=Milky 2=Dune
#   加 -DryRun 只看将要改动的内容，不写盘。
#
# 默认会给每个被修改的 save.sv 生成扩展名 .bak 的同目录备份；已存在同名备份时加时间戳。

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Path,

    [Parameter(Mandatory = $true)]
    [ValidateRange(0, 2)]
    [int]$Character,

    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression | Out-Null

function Read-SaveJson([string]$file) {
    $bytes = [IO.File]::ReadAllBytes($file)
    $ms = New-Object IO.MemoryStream(, $bytes)
    $gz = New-Object IO.Compression.GZipStream($ms, [IO.Compression.CompressionMode]::Decompress)
    $reader = New-Object IO.StreamReader($gz, [Text.Encoding]::ASCII)
    try { return $reader.ReadToEnd() } finally { $reader.Dispose(); $gz.Dispose(); $ms.Dispose() }
}

function Write-SaveJson([string]$file, [string]$json) {
    $ms = New-Object IO.MemoryStream
    $gz = New-Object IO.Compression.GZipStream($ms, [IO.Compression.CompressionLevel]::Optimal)
    $writer = New-Object IO.StreamWriter($gz, [Text.Encoding]::ASCII)
    try { $writer.Write($json) } finally { $writer.Dispose(); $gz.Dispose() }
    [IO.File]::WriteAllBytes($file, $ms.ToArray())
    $ms.Dispose()
}

# 只替换顶层 "character" 字段；存档是扁平 JSON，字段名唯一。
$pattern = '"character"\s*:\s*-?\d+'

$targets = @()
if (Test-Path -LiteralPath $Path -PathType Container) {
    $targets = Get-ChildItem -LiteralPath $Path -Recurse -Filter 'save.sv' -File |
        Where-Object { $_.FullName -notmatch '\\.old\\|\\.bak' }
} elseif (Test-Path -LiteralPath $Path -PathType Leaf) {
    $targets = @(Get-Item -LiteralPath $Path)
} else {
    throw "路径不存在: $Path"
}

if ($targets.Count -eq 0) {
    Write-Host "没有找到 save.sv" -ForegroundColor Yellow
    return
}

Write-Host "目标存档 $($targets.Count) 个，改为 character=$Character$(if ($DryRun) { '（DryRun，不写盘）' })"

foreach ($file in $targets) {
    try {
        $json = Read-SaveJson $file.FullName
        $matches = [regex]::Matches($json, $pattern)
        if ($matches.Count -ne 1) {
            Write-Host ("跳过 {0}：character 字段出现 {1} 次（期望 1 次）" -f $file.FullName, $matches.Count) -ForegroundColor Yellow
            continue
        }

        $before = $matches[0].Value
        $after = '"character":' + $Character
        if ($before -eq $after) {
            Write-Host ("跳过 {0}：已经是 {1}" -f $file.FullName, $after) -ForegroundColor DarkGray
        } else {
            Write-Host ("{0}`n  {1}  ->  {2}" -f $file.FullName, $before, $after) -ForegroundColor Green
            if (-not $DryRun) {
                $bak = $file.FullName + '.bak'
                if (Test-Path -LiteralPath $bak) {
                    $bak = $file.FullName + '.bak.' + (Get-Date -Format 'yyyyMMdd-HHmmss')
                }
                Copy-Item -LiteralPath $file.FullName -Destination $bak -Force
                Write-SaveJson $file.FullName ($json.Remove($matches[0].Index, $matches[0].Length).Insert($matches[0].Index, $after))
                Write-Host ("  备份 -> {0}" -f $bak) -ForegroundColor DarkGray
            }
        }

        # sidecar 同步：主存档修好后 sidecar 不该再覆盖回去
        $sidecar = $file.FullName + '.json'
        if (Test-Path -LiteralPath $sidecar) {
            $sc = Get-Content -LiteralPath $sidecar -Raw
            if ($sc -match $pattern) {
                $scNew = [regex]::Replace($sc, $pattern, $after, 1)
            } else {
                $scNew = $sc -replace '\}\s*$', (',' + $after + '}')
            }
            if ($scNew -ne $sc) {
                if (-not $DryRun) {
                    Copy-Item -LiteralPath $sidecar -Destination ($sidecar + '.bak') -Force
                    Set-Content -LiteralPath $sidecar -Value $scNew -Encoding UTF8 -NoNewline
                }
                Write-Host ("  sidecar 同步 character=$Character" ) -ForegroundColor DarkGray
            }
        }
    } catch {
        Write-Host ("失败 {0}：{1}" -f $file.FullName, $_.Exception.Message) -ForegroundColor Red
    }
}

Write-Host "完成。验证：启动游戏读档后角色应为目标角色；BepInEx 日志里应出现「读档角色兜底：character=…」。"
Write-Host "如果结果不对，直接用同目录下的 .bak 覆盖回来即可。"
