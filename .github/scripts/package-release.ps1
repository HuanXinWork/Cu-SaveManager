[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$VersionTag = "",

    [Parameter(Mandatory = $false)]
    [string]$OutputDir = "build/release-assets"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Reflection.Metadata
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$projPath = Join-Path $repoRoot "CuSaveManager.csproj"

if (-not (Test-Path -LiteralPath $projPath)) {
    throw "未找到项目文件：'$projPath'"
}

[xml]$projXml = Get-Content -LiteralPath $projPath -Raw -Encoding UTF8
$modVersion = ($projXml.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1).Trim()
$assemblyName = ($projXml.Project.PropertyGroup | ForEach-Object { $_.AssemblyName } | Where-Object { $_ } | Select-Object -First 1).Trim()

if ([string]::IsNullOrWhiteSpace($modVersion)) {
    throw "无法从 '$projPath' 读取非空的 <Version>。"
}
if ([string]::IsNullOrWhiteSpace($assemblyName)) {
    throw "无法从 '$projPath' 读取非空的 <AssemblyName>。"
}

$pkgPrefix = "CuSaveManager"
$pluginDir = "CuSaveManager"

if ([string]::IsNullOrWhiteSpace($VersionTag)) {
    $VersionTag = "v$modVersion"
}

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repoRoot "build/release-assets"
} elseif ([System.IO.Path]::IsPathRooted($OutputDir)) {
    $OutputDir = [System.IO.Path]::GetFullPath($OutputDir)
} else {
    $OutputDir = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDir))
}

function Test-CompiledAssemblyMetadata {
    param(
        [Parameter(Mandatory = $true)][string]$DllPath,
        [Parameter(Mandatory = $true)][string]$ExpectedVersion,
        [Parameter(Mandatory = $true)][bool]$ExpectedDisableOptimizations
    )

    if (-not (Test-Path -LiteralPath $DllPath)) {
        throw "未找到编译产物：'$DllPath'"
    }

    # 用 PEReader 静态解析 ECMA-335 元数据，避免在 .NET 8 宿主中加载 net48/Unity 运行时依赖并防止文件锁
    $fs = [System.IO.File]::OpenRead($DllPath)
    try {
        $peReader = [System.Reflection.PortableExecutable.PEReader]::new($fs)
        try {
            if (-not $peReader.HasMetadata) {
                throw "PE 文件不含 CLI 元数据：'$DllPath'"
            }
            $mdReader = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($peReader)

            $foundBuildInfo = $false
            $buildInfoVersion = $null
            foreach ($typeHandle in $mdReader.TypeDefinitions) {
                $typeDef = $mdReader.GetTypeDefinition($typeHandle)
                if ($mdReader.GetString($typeDef.Name) -ne "BuildInfo") {
                    continue
                }
                $foundBuildInfo = $true
                foreach ($fieldHandle in $typeDef.GetFields()) {
                    $fieldDef = $mdReader.GetFieldDefinition($fieldHandle)
                    if ($mdReader.GetString($fieldDef.Name) -ne "Version") {
                        continue
                    }
                    $constHandle = $fieldDef.GetDefaultValue()
                    if ($constHandle.IsNil) {
                        throw "BuildInfo.Version 缺少常量默认值 ($DllPath)"
                    }
                    $constant = $mdReader.GetConstant($constHandle)
                    if ($constant.TypeCode -ne [System.Reflection.Metadata.ConstantTypeCode]::String) {
                        throw "BuildInfo.Version 常量类型不匹配 ($DllPath): $($constant.TypeCode)"
                    }
                    $blobReader = $mdReader.GetBlobReader($constant.Value)
                    $buildInfoVersion = $blobReader.ReadUTF16($blobReader.Length)
                    break
                }
                break
            }

            if (-not $foundBuildInfo -or $null -eq $buildInfoVersion) {
                throw "未能在元数据中找到 BuildInfo.Version 常量字段 ($DllPath)"
            }
            if ($buildInfoVersion -ne $ExpectedVersion) {
                throw "BuildInfo.Version 不匹配 ($DllPath): 实际='$buildInfoVersion', 期望='$ExpectedVersion'"
            }

            $asmDef = $mdReader.GetAssemblyDefinition()
            $foundDebuggable = $false
            $hasDisableOptimizations = $false
            foreach ($caHandle in $asmDef.GetCustomAttributes()) {
                $ca = $mdReader.GetCustomAttribute($caHandle)
                $ctorHandle = $ca.Constructor
                $attrTypeName = ""
                $attrTypeNs = ""
                $sigHandle = [System.Reflection.Metadata.BlobHandle]::new()

                if ($ctorHandle.Kind -eq [System.Reflection.Metadata.HandleKind]::MemberReference) {
                    $memberRef = $mdReader.GetMemberReference([System.Reflection.Metadata.MemberReferenceHandle]$ctorHandle)
                    $sigHandle = $memberRef.Signature
                    if ($memberRef.Parent.Kind -eq [System.Reflection.Metadata.HandleKind]::TypeReference) {
                        $typeRef = $mdReader.GetTypeReference([System.Reflection.Metadata.TypeReferenceHandle]$memberRef.Parent)
                        $attrTypeName = $mdReader.GetString($typeRef.Name)
                        $attrTypeNs = $mdReader.GetString($typeRef.Namespace)
                    }
                } elseif ($ctorHandle.Kind -eq [System.Reflection.Metadata.HandleKind]::MethodDefinition) {
                    $methodDef = $mdReader.GetMethodDefinition([System.Reflection.Metadata.MethodDefinitionHandle]$ctorHandle)
                    $sigHandle = $methodDef.Signature
                    $declType = $mdReader.GetTypeDefinition($methodDef.GetDeclaringType())
                    $attrTypeName = $mdReader.GetString($declType.Name)
                    $attrTypeNs = $mdReader.GetString($declType.Namespace)
                }

                if ($attrTypeNs -ne "System.Diagnostics" -or $attrTypeName -ne "DebuggableAttribute") {
                    continue
                }

                $foundDebuggable = $true
                $sigReader = $mdReader.GetBlobReader($sigHandle)
                $null = $sigReader.ReadByte()
                $paramCount = $sigReader.ReadCompressedInteger()

                $valReader = $mdReader.GetBlobReader($ca.Value)
                $prolog = $valReader.ReadUInt16()
                if ($prolog -ne 0x0001) {
                    throw "DebuggableAttribute Blob Prolog 异常 ($DllPath): 0x$($prolog.ToString('X4'))"
                }
                if ($paramCount -eq 1) {
                    $modes = $valReader.ReadInt32()
                    $hasDisableOptimizations = (($modes -band 0x0100) -ne 0)
                } elseif ($paramCount -eq 2) {
                    $null = $valReader.ReadBoolean()
                    $hasDisableOptimizations = $valReader.ReadBoolean()
                } else {
                    throw "不支持的 DebuggableAttribute 构造参数数量 ($DllPath): $paramCount"
                }
                break
            }

            if (-not $foundDebuggable) {
                throw "未能在 AssemblyDefinition 上找到 DebuggableAttribute ($DllPath)"
            }
            if ($hasDisableOptimizations -ne $ExpectedDisableOptimizations) {
                throw "DebuggableAttribute DisableOptimizations 标记不匹配 ($DllPath): 实际=$hasDisableOptimizations, 期望=$ExpectedDisableOptimizations"
            }

            Write-Host "    [校验通过] $(Split-Path -Leaf $DllPath): BuildInfo.Version=$buildInfoVersion, DisableOptimizations=$hasDisableOptimizations" -ForegroundColor DarkGreen
        } finally {
            $peReader.Dispose()
        }
    } finally {
        $fs.Dispose()
    }
}

function New-PluginZipArchive {
    param(
        [Parameter(Mandatory = $true)][string]$ZipPath,
        [Parameter(Mandatory = $true)][hashtable[]]$Entries
    )

    if (Test-Path -LiteralPath $ZipPath) {
        Remove-Item -LiteralPath $ZipPath -Force
    }

    # 显式使用正斜杠写入 ZIP 条目名，确保跨平台解压识别一致的目录层级
    $fs = [System.IO.File]::Open($ZipPath, [System.IO.FileMode]::CreateNew)
    try {
        $zip = [System.IO.Compression.ZipArchive]::new($fs, [System.IO.Compression.ZipArchiveMode]::Create, $false)
        try {
            foreach ($item in $Entries) {
                $src = [string]$item.Source
                $entryName = [string]$item.EntryName
                if (-not (Test-Path -LiteralPath $src)) {
                    throw "打包缺失源文件：'$src'"
                }
                if ($entryName.Contains('\')) {
                    throw "ZIP 条目名禁止包含反斜杠：'$entryName'"
                }
                [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                    $zip,
                    $src,
                    $entryName,
                    [System.IO.Compression.CompressionLevel]::Optimal
                ) | Out-Null
            }
        } finally {
            $zip.Dispose()
        }
    } finally {
        $fs.Dispose()
    }

    $verifyZip = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        if ($verifyZip.Entries.Count -ne $Entries.Count) {
            throw "ZIP 条目数不匹配 ($ZipPath): 实际=$($verifyZip.Entries.Count), 期望=$($Entries.Count)"
        }
        foreach ($entry in $verifyZip.Entries) {
            if ($entry.FullName.Contains('\')) {
                throw "ZIP 包含反斜杠路径条目 ($ZipPath): '$($entry.FullName)'"
            }
            if (-not $entry.FullName.StartsWith("BepInEx/plugins/", [System.StringComparison]::Ordinal)) {
                throw "ZIP 条目根路径异常 ($ZipPath): '$($entry.FullName)'"
            }
            if ($entry.Length -le 0) {
                throw "ZIP 条目内容为空 ($ZipPath): '$($entry.FullName)'"
            }
        }
    } finally {
        $verifyZip.Dispose()
    }
}

Write-Host ">>> 正在执行 dotnet restore ..." -ForegroundColor Cyan
& dotnet restore $projPath --nologo -v minimal | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "dotnet restore 失败，退出码：$LASTEXITCODE"
}

foreach ($cfg in @("Release", "Debug")) {
    Write-Host ">>> 正在构建 $cfg (VersionTag=$VersionTag, Version=$modVersion) ..." -ForegroundColor Cyan
    & dotnet build $projPath -c $cfg --no-incremental --nologo -warnaserror | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build $cfg 失败，退出码：$LASTEXITCODE"
    }
}

$releaseDll = Join-Path $repoRoot "bin\Release\$assemblyName.dll"
$debugDll = Join-Path $repoRoot "bin\Debug\$assemblyName.dll"
$debugPdb = Join-Path $repoRoot "bin\Debug\$assemblyName.pdb"

if (-not (Test-Path -LiteralPath $debugPdb)) {
    throw "Debug 构建未生成调试符号文件：'$debugPdb'"
}
$pdbSize = (Get-Item -LiteralPath $debugPdb).Length
if ($pdbSize -le 0) {
    throw "Debug 调试符号文件为空：'$debugPdb'"
}

Test-CompiledAssemblyMetadata -DllPath $releaseDll -ExpectedVersion $modVersion -ExpectedDisableOptimizations $false
Test-CompiledAssemblyMetadata -DllPath $debugDll -ExpectedVersion $modVersion -ExpectedDisableOptimizations $true
Write-Host "    [校验通过] $(Split-Path -Leaf $debugPdb): Size=$pdbSize bytes" -ForegroundColor DarkGreen

$commonEntries = [System.Collections.Generic.List[hashtable]]::new()
foreach ($docName in @("README.md", "README.en.md", "changes.md", "changes.en.md")) {
    $docPath = Join-Path $repoRoot $docName
    if (Test-Path -LiteralPath $docPath) {
        $commonEntries.Add(@{
            Source    = $docPath
            EntryName = "BepInEx/plugins/$pluginDir/$docName"
        })
    }
}
foreach ($localeName in @("en.json", "zh.json")) {
    $localePath = Join-Path $repoRoot "references\StandartLangs\$localeName"
    if (-not (Test-Path -LiteralPath $localePath)) {
        throw "缺少默认语言包文件：'$localePath'"
    }
    $commonEntries.Add(@{
        Source    = $localePath
        EntryName = "BepInEx/plugins/CuSaveManagerLocales/$localeName"
    })
}

$releaseEntries = @(
    @{ Source = $releaseDll; EntryName = "BepInEx/plugins/$pluginDir/$assemblyName.dll" }
) + $commonEntries.ToArray()

$debugEntries = @(
    @{ Source = $debugDll; EntryName = "BepInEx/plugins/$pluginDir/$assemblyName.dll" },
    @{ Source = $debugPdb; EntryName = "BepInEx/plugins/$pluginDir/$assemblyName.pdb" }
) + $commonEntries.ToArray()

if (Test-Path -LiteralPath $OutputDir) {
    Remove-Item -LiteralPath $OutputDir -Recurse -Force
}
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

$releaseZipName = "$pkgPrefix-$VersionTag.zip"
$debugZipName = "$pkgPrefix-$VersionTag+debug.zip"
$singleReleaseDllName = "$assemblyName.dll"
$singleDebugDllName = "$assemblyName+debug.dll"

New-PluginZipArchive -ZipPath (Join-Path $OutputDir $releaseZipName) -Entries $releaseEntries
New-PluginZipArchive -ZipPath (Join-Path $OutputDir $debugZipName) -Entries $debugEntries

Copy-Item -LiteralPath $releaseDll -Destination (Join-Path $OutputDir $singleReleaseDllName) -Force
Copy-Item -LiteralPath $debugDll -Destination (Join-Path $OutputDir $singleDebugDllName) -Force

$assetNames = @(
    $releaseZipName,
    $debugZipName,
    $singleReleaseDllName,
    $singleDebugDllName
)

$hashLines = foreach ($name in $assetNames) {
    $filePath = Join-Path $OutputDir $name
    if (-not (Test-Path -LiteralPath $filePath) -or (Get-Item -LiteralPath $filePath).Length -le 0) {
        throw "产物缺失或为空：'$filePath'"
    }
    $sha256 = (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$sha256  $name"
}

$sumsPath = Join-Path $OutputDir "SHA256SUMS.txt"
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText($sumsPath, (($hashLines -join "`n") + "`n"), $utf8NoBom)

Write-Host ">>> 打包完成，5 项标准发布资产已输出至: $OutputDir" -ForegroundColor Green
$hashLines | ForEach-Object { Write-Host "    $_" }
