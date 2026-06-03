param(
    [Parameter(Mandatory = $true)][string]$OriginalDir,
    [Parameter(Mandatory = $true)][string]$GeneratedDir,
    [Parameter(Mandatory = $false)][string]$OutputReport = ""
)

$ErrorActionPreference = "Stop"

function Normalize-FileKey([string]$fullPath, [string]$root) {
    $rootPath = [System.IO.Path]::GetFullPath($root)
    $filePath = [System.IO.Path]::GetFullPath($fullPath)
    if (-not $rootPath.EndsWith([System.IO.Path]::DirectorySeparatorChar)) {
        $rootPath += [System.IO.Path]::DirectorySeparatorChar
    }
    $rel = $filePath
    if ($filePath.StartsWith($rootPath, [System.StringComparison]::OrdinalIgnoreCase)) {
        $rel = $filePath.Substring($rootPath.Length)
    }
    $rel = $rel.Replace("\", "/")
    $name = [System.IO.Path]::GetFileNameWithoutExtension($rel)
    $name = $name -replace '\.generated$', ''
    # Canonical file key: reduce naming noise (underscore/case/punctuation)
    $name = ($name.ToLowerInvariant() -replace '[^a-z0-9]', '')

    # Report/TextIO section numbering is not stable between legacy migrated code
    # and generated output. Treat C1/C2 print sections as the same logical file key.
    $name = $name -replace 'c[12]printreportdesigner$', 'cprintreportdesigner'
    $name = $name -replace 'c[12]printreport$', 'cprintreport'

    # Some legacy migrated views repeat the task name in the file name
    # (e.g. TaskNameTaskName / TaskNameTaskNameDesigner) where the generated
    # code emits the same view as TaskNameView / TaskNameViewDesigner.
    if ($name.EndsWith('designer')) {
        $base = $name.Substring(0, $name.Length - 'designer'.Length)
        if (($base.Length % 2) -eq 0) {
            $half = $base.Substring(0, [int]($base.Length / 2))
            if ($half -eq $base.Substring([int]($base.Length / 2))) {
                return $half + 'viewdesigner'
            }
        }
    } elseif (($name.Length % 2) -eq 0) {
        $half = $name.Substring(0, [int]($name.Length / 2))
        if ($half -eq $name.Substring([int]($name.Length / 2))) {
            return $half + 'view'
        }
    }

    return $name
}

function Get-CsFiles([string]$root) {
    if (-not (Test-Path $root)) { return @() }
    return Get-ChildItem -Path $root -Recurse -Filter *.cs -File |
        Where-Object {
            $_.FullName -notmatch '[\\\/](bin|obj|\.vs)[\\\/]' -and
            $_.Name -notmatch 'AssemblyAttributes\.cs$'
        }
}

function Get-Lines([string]$path) {
    return Get-Content -Encoding UTF8 $path
}

function Extract-DesignerDeclMap([string[]]$lines) {
    $map = @{}
    foreach ($line in $lines) {
        if ($line -match '^\s*([\w\.]+)\s+([A-Za-z_]\w*)\s*;') {
            $type = $matches[1]
            $name = $matches[2]
            $map[$name] = $type
        }
    }
    return $map
}

function Extract-NewMap([string[]]$lines) {
    $map = @{}
    foreach ($line in $lines) {
        if ($line -match '^\s*([A-Za-z_]\w*)\s*=\s*new\s+([\w\.]+)\s*\(\s*\)\s*;') {
            $name = $matches[1]
            $type = $matches[2]
            $map[$name] = $type
        }
    }
    return $map
}

function Extract-PropertyMap([string[]]$lines) {
    $map = @{}
    $propRegex = '^\s*([A-Za-z_]\w*)\.(Data|ToolTip|BoundTo|Style|Format|Text|Name|Rtf|UseRtf)\s*=\s*(.+);'
    foreach ($line in $lines) {
        if ($line -match $propRegex) {
            $ctrl = $matches[1]
            $prop = $matches[2]
            $val = $matches[3].Trim()
            $key = "$ctrl.$prop"
            $map[$key] = $val
        }
    }
    return $map
}

function Extract-DesignerClassName([string[]]$lines) {
    foreach ($line in $lines) {
        if ($line -match '^\s*partial\s+class\s+([A-Za-z_]\w*)\s*$') {
            return $matches[1]
        }
    }
    return ""
}

function Extract-ControlNameByVar([string[]]$lines) {
    $map = @{}
    foreach ($line in $lines) {
        if ($line -match '^\s*([A-Za-z_]\w*)\.Name\s*=\s*"([^"]+)"\s*;') {
            $map[$matches[1]] = $matches[2]
        }
    }
    return $map
}

function Normalize-BoundToValue([string]$val, $varToName) {
    if ($val -match 'ControlBinding\(([A-Za-z_]\w*)\)') {
        $var = $matches[1]
        if ($varToName.ContainsKey($var)) {
            return "ControlBinding(" + $varToName[$var] + ")"
        }
    }
    return $val
}

function Extract-DesignerSemanticMaps([string[]]$lines) {
    $declByVar = Extract-DesignerDeclMap $lines
    $newByVar = Extract-NewMap $lines
    $propsByVar = Extract-PropertyMap $lines
    $nameByVar = Extract-ControlNameByVar $lines
    $rootClassName = Extract-DesignerClassName $lines

    $decl = @{}
    foreach ($kv in $declByVar.GetEnumerator()) {
        $var = $kv.Key
        $controlKey = if ($nameByVar.ContainsKey($var)) { $nameByVar[$var] } else { $var }
        $decl[$controlKey] = $kv.Value
    }

    $newMap = @{}
    foreach ($kv in $newByVar.GetEnumerator()) {
        $var = $kv.Key
        $controlKey = if ($nameByVar.ContainsKey($var)) { $nameByVar[$var] } else { $var }
        $newMap[$controlKey] = $kv.Value
    }

    $props = @{}
    foreach ($kv in $propsByVar.GetEnumerator()) {
        $dot = $kv.Key.IndexOf('.')
        if ($dot -lt 0) { continue }
        $var = $kv.Key.Substring(0, $dot)
        $prop = $kv.Key.Substring($dot + 1)
        $controlKey = if ($nameByVar.ContainsKey($var)) { $nameByVar[$var] } else { $var }
        $val = $kv.Value
        if ($prop -eq "BoundTo") { $val = Normalize-BoundToValue $val $nameByVar }
        $props["$controlKey.$prop"] = $val
    }

    # Root form properties appear as unqualified assignments in InitializeComponent:
    # Text = "..."; Name = "...";
    if (-not [string]::IsNullOrWhiteSpace($rootClassName)) {
        foreach ($line in $lines) {
            if ($line -match '^\s*(Text|Name)\s*=\s*(.+);') {
                $prop = $matches[1]
                $val = $matches[2].Trim()
                $props["$rootClassName.$prop"] = $val
            }
        }
    }

    return @{
        Decl = $decl
        New = $newMap
        Props = $props
        NameByVar = $nameByVar
    }
}

function Extract-MethodSignatures([string[]]$lines) {
    $methods = @{}
    $rx = '^\s*(public|internal|protected|private)\s+(?:override\s+)?(?:static\s+)?[\w<>\.\?\[\],]+\s+([A-Za-z_]\w*)\s*\(([^)]*)\)'
    foreach ($line in $lines) {
        if ($line -match $rx) {
            $name = $matches[2]
            $args = Normalize-MethodArgs (($matches[3] -replace '\s+', ' ').Trim())
            $methods["$name($args)"] = $true
        }
    }
    return $methods
}

function Normalize-MethodArgs([string]$argsText) {
    if ([string]::IsNullOrWhiteSpace($argsText)) { return "" }
    $parts = $argsText -split '\s*,\s*'
    $normalized = @()
    foreach ($part in $parts) {
        $p = $part.Trim()
        if ($p -match '^(params|ref|out|in)\s+(.+)$') {
            $modifier = $matches[1]
            $p = $matches[2].Trim()
            if ($p -match '^(.+?)\s+[A-Za-z_]\w*(\s*=\s*.+)?$') {
                $type = $matches[1].Trim()
                $default = $matches[2]
                $normalized += ($modifier + ' ' + $type + $default)
                continue
            }
        }
        if ($p -match '^(.+?)\s+[A-Za-z_]\w*(\s*=\s*.+)?$') {
            $type = $matches[1].Trim()
            $default = $matches[2]
            $normalized += ($type + $default)
            continue
        }
        $normalized += $p
    }
    return ($normalized -join ', ')
}

function Extract-ControlsAddMap([string[]]$lines, $nameByVar) {
    $map = @{}
    foreach ($line in $lines) {
        if ($line -match '^\s*([A-Za-z_]\w*)\.Controls\.Add\(([A-Za-z_]\w*)\)\s*;') {
            $containerVar = $matches[1]
            $childVar = $matches[2]
            $container = if ($nameByVar.ContainsKey($containerVar)) { $nameByVar[$containerVar] } else { $containerVar }
            $child = if ($nameByVar.ContainsKey($childVar)) { $nameByVar[$childVar] } else { $childVar }
            $map["$container->$child"] = $true
        } elseif ($line -match '^\s*Controls\.Add\(([A-Za-z_]\w*)\)\s*;') {
            $childVar = $matches[1]
            $child = if ($nameByVar.ContainsKey($childVar)) { $nameByVar[$childVar] } else { $childVar }
            $map["<root>->$child"] = $true
        }
    }
    return $map
}

function Normalize-BodyText([string]$txt) {
    $v = $txt
    $v = [regex]::Replace($v, '//.*', '')
    $v = [regex]::Replace($v, '/\*.*?\*/', '', [System.Text.RegularExpressions.RegexOptions]::Singleline)
    $v = [regex]::Replace($v, '\s+', '')
    return $v
}

function Extract-MethodBodies([string[]]$lines) {
    $result = @{}
    $sigRegex = '^\s*(public|internal|protected|private)\s+(?:override\s+)?(?:static\s+)?[\w<>\.\?\[\],]+\s+([A-Za-z_]\w*)\s*\(([^)]*)\)'
    $i = 0
    while ($i -lt $lines.Count) {
        $line = $lines[$i]
        if ($line -match $sigRegex) {
            $name = $matches[2]
            $args = ($matches[3] -replace '\s+', ' ').Trim()
            $sig = "$name($args)"

            # Expression-bodied members or abstract/interface-like declarations do not
            # have a block body. Treat the current line as the body and continue,
            # otherwise the scanner may consume the next method block and produce
            # false "missing critical method" findings.
            if (($line -match '=>') -and ($line -notmatch '\{') -and $line.TrimEnd().EndsWith(';')) {
                $result[$sig] = Normalize-BodyText $line
                $i++
                continue
            }

            $bodyStart = -1
            $braceCount = 0
            $j = $i
            while ($j -lt $lines.Count) {
                $openCount = ([regex]::Matches($lines[$j], '\{')).Count
                $closeCount = ([regex]::Matches($lines[$j], '\}')).Count
                if ($bodyStart -lt 0 -and $openCount -gt 0) {
                    $bodyStart = $j
                }
                if ($bodyStart -ge 0) {
                    $braceCount += $openCount
                    $braceCount -= $closeCount
                    if ($braceCount -le 0) {
                        break
                    }
                }
                $j++
            }

            if ($bodyStart -ge 0 -and $j -lt $lines.Count) {
                $bodyRaw = ($lines[$bodyStart..$j] -join "`n")
                $result[$sig] = Normalize-BodyText $bodyRaw
                $i = $j
            }
        }
        $i++
    }
    return $result
}

function Normalize-CodeForFingerprint([string[]]$lines) {
    $txt = ($lines -join "`n")
    $txt = [regex]::Replace($txt, '//.*', '')
    $txt = [regex]::Replace($txt, '/\*.*?\*/', '', [System.Text.RegularExpressions.RegexOptions]::Singleline)
    $txt = [regex]::Replace($txt, '\s+', '')
    return $txt
}

function Compare-CriticalMethods($origBodies, $genBodies, $origMethods, $genMethods, [ref]$findings) {
    $criticalNames = @(
        "InitializeComponent",
        "OnLoad",
        "OnStart",
        "OnLeaveRow",
        "OnEnd",
        "InitializeDataView",
        "InitializeHandlers",
        "InitializeDataViewAndUserFlow"
    )

    $origCriticalSignatures = @{}
    foreach ($k in $origMethods.Keys) {
        $name = $k.Split('(')[0]
        if ($criticalNames -contains $name) { $origCriticalSignatures[$k] = $true }
    }
    $genCriticalSignatures = @{}
    foreach ($k in $genMethods.Keys) {
        $name = $k.Split('(')[0]
        if ($criticalNames -contains $name) { $genCriticalSignatures[$k] = $true }
    }

    foreach ($k in $origCriticalSignatures.Keys) {
        if (-not $genCriticalSignatures.ContainsKey($k)) {
            $findings.Value += [pscustomobject]@{
                Category = "Metodo.Critico"
                Severity = "High"
                Detail = "Metodo critico ausente no gerado: $k"
            }
            continue
        }

        if ($origBodies.ContainsKey($k) -and $genBodies.ContainsKey($k) -and $origBodies[$k] -ne $genBodies[$k]) {
            $findings.Value += [pscustomobject]@{
                Category = "Metodo.Critico"
                Severity = "Medium"
                Detail = "Corpo diferente em metodo critico: $k"
            }
        }
    }
    foreach ($k in $genCriticalSignatures.Keys) {
        if (-not $origCriticalSignatures.ContainsKey($k)) {
            $findings.Value += [pscustomobject]@{
                Category = "Metodo.Critico"
                Severity = "Low"
                Detail = "Metodo critico extra no gerado: $k"
            }
        }
    }
}

function Compare-MapValues($orig, $gen, [string]$label, [ref]$findings) {
    foreach ($k in $orig.Keys) {
        if ($label -eq "Designer.New" -and $k -eq "components") { continue }
        if ($label -eq "Designer.Declaracoes" -and $k -eq "components") { continue }
        if (-not $gen.ContainsKey($k)) {
            $findings.Value += [pscustomobject]@{
                Category = $label
                Severity = "Medium"
                Detail = "Ausente no gerado: $k -> $($orig[$k])"
            }
            continue
        }
        if ($orig[$k] -ne $gen[$k]) {
            $findings.Value += [pscustomobject]@{
                Category = $label
                Severity = "Low"
                Detail = "Valor diferente em $k | orig='$($orig[$k])' | gerado='$($gen[$k])'"
            }
        }
    }
    foreach ($k in $gen.Keys) {
        if ($label -eq "Designer.New" -and $k -eq "components") { continue }
        if ($label -eq "Designer.Declaracoes" -and $k -eq "components") { continue }
        if (-not $orig.ContainsKey($k)) {
            $findings.Value += [pscustomobject]@{
                Category = $label
                Severity = "Low"
                Detail = "Extra no gerado: $k -> $($gen[$k])"
            }
        }
    }
}

function Compare-Set($orig, $gen, [string]$label, [ref]$findings) {
    foreach ($k in $orig.Keys) {
        if (-not $gen.ContainsKey($k)) {
            $findings.Value += [pscustomobject]@{
                Category = $label
                Severity = "Medium"
                Detail = "Assinatura ausente no gerado: $k"
            }
        }
    }
    foreach ($k in $gen.Keys) {
        if (-not $orig.ContainsKey($k)) {
            $findings.Value += [pscustomobject]@{
                Category = $label
                Severity = "Low"
                Detail = "Assinatura extra no gerado: $k"
            }
        }
    }
}

function Is-FunctionalFinding($f) {
    if ($f.Category -eq "Arquivo") {
        if ($f.Detail -match 'displaytabledisplaylist(designer)?|displaylistview(designer)?') { return $false }
        if ($f.Detail -match 'en0[56].*c1createfile(designer)?') { return $false }
        return $true
    }
    if ($f.Category -eq "Classe.Metodos") {
        if ($f.Detail -match 'ButtonClickEventArgs') { return $false }
    }
    if ($f.Category -eq "Metodo.Critico") {
        if ($f.Detail -match 'ausente no gerado') { return $true }
        return $false
    }
    if ($f.Category -eq "Classe.Metodos") {
        if ($f.Detail -match 'Assinatura ausente no gerado') { return $true }
        return $false
    }
    if ($f.Category -in @("Designer.New", "Designer.HierarquiaControlsAdd")) {
        if ($f.Detail -match 'Ausente no gerado|Assinatura ausente no gerado') { return $true }
        return $false
    }
    if ($f.Category -eq "Designer.Propriedades") {
        if ($f.Detail -match 'Ausente no gerado: .*?\.(Data|BoundTo|ToolTip)\b') { return $true }
        return $false
    }
    # Noise filters (mostly naming/styling chatter)
    if ($f.Category -eq "Designer.Declaracoes") { return $false }
    if ($f.Category -eq "Designer.Propriedades" -and $f.Detail -match '\.(Name|Style|Rtf|UseRtf|Format)\b') { return $false }
    return $false
}

function Get-FunctionalScore($f) {
    $score = 0
    switch ($f.Severity) {
        "High" { $score += 100 }
        "Medium" { $score += 50 }
        default { $score += 10 }
    }
    switch ($f.Category) {
        "Metodo.Critico" { $score += 80 }
        "Classe.Metodos" { $score += 50 }
        "Designer.HierarquiaControlsAdd" { $score += 40 }
        "Designer.New" { $score += 35 }
        "Designer.Propriedades" { $score += 20 }
        "Arquivo" { $score += 30 }
    }
    return $score
}

if (-not $OutputReport) {
    $OutputReport = Join-Path $GeneratedDir "RELATORIO_DIFF_COMPLETO.md"
}

$origFiles = Get-CsFiles $OriginalDir
$genFiles = Get-CsFiles $GeneratedDir

$origByKey = @{}
foreach ($f in $origFiles) {
    $k = Normalize-FileKey $f.FullName $OriginalDir
    if (-not $origByKey.ContainsKey($k)) { $origByKey[$k] = @() }
    $origByKey[$k] += $f.FullName
}

$genByKey = @{}
foreach ($f in $genFiles) {
    $k = Normalize-FileKey $f.FullName $GeneratedDir
    if (-not $genByKey.ContainsKey($k)) { $genByKey[$k] = @() }
    $genByKey[$k] += $f.FullName
}

$origKeySet = @($origByKey.Keys) | Sort-Object -Unique
$genKeySet = @($genByKey.Keys) | Sort-Object -Unique
$commonKeys = @($origKeySet | Where-Object { $genByKey.ContainsKey($_) }) | Sort-Object -Unique
$missingInGenerated = @($origKeySet | Where-Object { -not $genByKey.ContainsKey($_) }) | Sort-Object -Unique
$missingInOriginal = @($genKeySet | Where-Object { -not $origByKey.ContainsKey($_) }) | Sort-Object -Unique
$allKeys = @($origKeySet + $genKeySet) | Sort-Object -Unique
$findings = @()
$fileStats = @()

foreach ($k in $missingInOriginal) {
    $findings += [pscustomobject]@{ Category = "Arquivo"; Severity = "Medium"; Detail = "Arquivo sem par no original: $k" }
}
foreach ($k in $missingInGenerated) {
    $findings += [pscustomobject]@{ Category = "Arquivo"; Severity = "High"; Detail = "Arquivo sem par no gerado: $k" }
}

foreach ($k in $commonKeys) {
    $origList = @($origByKey[$k])
    $genList = @($genByKey[$k])

    $orig = $origList[0]
    $gen = $genList[0]
    $origLines = Get-Lines $orig
    $genLines = Get-Lines $gen

    $isDesigner = $orig.ToLowerInvariant().EndsWith(".designer.cs") -or $gen.ToLowerInvariant().EndsWith(".designer.cs")
    $localFindings = @()

    if ($isDesigner) {
        $origSem = Extract-DesignerSemanticMaps $origLines
        $genSem = Extract-DesignerSemanticMaps $genLines

        $origDecl = $origSem.Decl
        $genDecl = $genSem.Decl
        Compare-MapValues $origDecl $genDecl "Designer.Declaracoes" ([ref]$localFindings)

        $origNew = $origSem.New
        $genNew = $genSem.New
        Compare-MapValues $origNew $genNew "Designer.New" ([ref]$localFindings)

        $origProps = $origSem.Props
        $genProps = $genSem.Props
        Compare-MapValues $origProps $genProps "Designer.Propriedades" ([ref]$localFindings)

        $origAdd = Extract-ControlsAddMap $origLines $origSem.NameByVar
        $genAdd = Extract-ControlsAddMap $genLines $genSem.NameByVar
        Compare-Set $origAdd $genAdd "Designer.HierarquiaControlsAdd" ([ref]$localFindings)
    } else {
        $origMethods = Extract-MethodSignatures $origLines
        $genMethods = Extract-MethodSignatures $genLines
        Compare-Set $origMethods $genMethods "Classe.Metodos" ([ref]$localFindings)

        $origBodies = Extract-MethodBodies $origLines
        $genBodies = Extract-MethodBodies $genLines
        Compare-CriticalMethods $origBodies $genBodies $origMethods $genMethods ([ref]$localFindings)

        $origFp = Normalize-CodeForFingerprint $origLines
        $genFp = Normalize-CodeForFingerprint $genLines
        if ($origFp -ne $genFp) {
            $localFindings += [pscustomobject]@{
                Category = "Classe.Corpo"
                Severity = "Low"
                Detail = "Corpo de arquivo difere apos normalizacao"
            }
        }
    }

    foreach ($f in $localFindings) {
        $findings += [pscustomobject]@{
            Category = $f.Category
            Severity = $f.Severity
            Detail = "[$k] $($f.Detail)"
        }
    }

    $fileStats += [pscustomobject]@{
        FileKey = $k
        IsDesigner = $isDesigner
        Findings = $localFindings.Count
    }
}

$bySeverity = $findings | Group-Object Severity | Sort-Object Name
$functionalFindings = @($findings | Where-Object { Is-FunctionalFinding $_ })
$functionalTop = @($functionalFindings |
    Select-Object *, @{Name="Score";Expression={ Get-FunctionalScore $_ }} |
    Sort-Object Score -Descending |
    Select-Object -First 200)

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine("# Relatorio de Diff Fino (Classes + Designer)")
[void]$sb.AppendLine()
[void]$sb.AppendLine("- Original: $OriginalDir")
[void]$sb.AppendLine("- Gerado: $GeneratedDir")
[void]$sb.AppendLine("- Total de arquivos mapeados: $($allKeys.Count)")
[void]$sb.AppendLine("- Total de achados: $($findings.Count)")
[void]$sb.AppendLine()
[void]$sb.AppendLine("## Achados por Severidade")
foreach ($g in $bySeverity) {
    [void]$sb.AppendLine("- $($g.Name): $($g.Count)")
}
[void]$sb.AppendLine()
[void]$sb.AppendLine("## Divergencias Funcionais Priorizadas")
[void]$sb.AppendLine("- Total filtrado: $($functionalFindings.Count)")
if ($functionalTop.Count -eq 0) {
    [void]$sb.AppendLine("Nenhuma divergencia funcional priorizada detectada.")
} else {
    foreach ($f in $functionalTop) {
        [void]$sb.AppendLine("- [Score $($f.Score)] [$($f.Severity)] [$($f.Category)] $($f.Detail)")
    }
}
[void]$sb.AppendLine()
[void]$sb.AppendLine("## Top Arquivos com Diferencas")
$top = $fileStats | Where-Object { $_.Findings -gt 0 } | Sort-Object Findings -Descending | Select-Object -First 50
if ($top.Count -eq 0) {
    [void]$sb.AppendLine("Sem diferencas detectadas pelo diff fino.")
} else {
    foreach ($t in $top) {
        [void]$sb.AppendLine("- $($t.FileKey): $($t.Findings)")
    }
}
[void]$sb.AppendLine()
[void]$sb.AppendLine("## Lista Detalhada")
if ($findings.Count -eq 0) {
    [void]$sb.AppendLine("Sem diferencas detectadas.")
} else {
    foreach ($f in $findings) {
        [void]$sb.AppendLine("- [$($f.Severity)] [$($f.Category)] $($f.Detail)")
    }
}

$outDir = [System.IO.Path]::GetDirectoryName($OutputReport)
if (-not [string]::IsNullOrWhiteSpace($outDir)) {
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
}
[System.IO.File]::WriteAllText($OutputReport, $sb.ToString(), [System.Text.UTF8Encoding]::new($false))

Write-Host "Diff fino gerado em: $OutputReport"
