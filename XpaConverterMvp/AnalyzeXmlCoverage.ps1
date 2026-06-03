param(
    [Parameter(Mandatory=$true)][string]$XmlPath,
    [Parameter(Mandatory=$true)][string]$GeneratedDir,
    [Parameter(Mandatory=$false)][string]$OriginalDir = "",
    [Parameter(Mandatory=$false)][string]$OutputReport = ""
)

$ErrorActionPreference = "Stop"

function Get-Text([string]$path) {
    return Get-Content -Raw -Encoding UTF8 $path
}

function Add-Count($map, [string]$key) {
    if ([string]::IsNullOrWhiteSpace($key)) { return }
    if ($map.ContainsKey($key)) { $map[$key]++ } else { $map[$key] = 1 }
}

function Collect-XmlInventory([xml]$xmlDoc) {
    $elements = @{}
    $attributes = @{}
    $models = @{}
    $logicLineKinds = @{}

    $nodes = $xmlDoc.SelectNodes("//*")
    foreach ($node in $nodes) {
        Add-Count $elements $node.Name
        if ($node.Attributes) {
            foreach ($attr in $node.Attributes) {
                Add-Count $attributes $attr.Name
            }
        }
        if ($node.Name -eq "PropertyList") {
            $m = $node.GetAttribute("model")
            if ($m) { Add-Count $models $m }
        }
    }

    $logicLines = $xmlDoc.SelectNodes("//LogicLine")
    foreach ($line in $logicLines) {
        $first = $null
        foreach ($child in $line.ChildNodes) {
            if ($child.NodeType -eq [System.Xml.XmlNodeType]::Element) { $first = $child; break }
        }
        if ($first -ne $null) { Add-Count $logicLineKinds $first.LocalName }
    }

    return @{
        Elements = $elements
        Attributes = $attributes
        Models = $models
        LogicLineKinds = $logicLineKinds
    }
}

function Get-SwitchCaseLabels([string]$parserText) {
    $labels = @{}
    $rx = [regex]'case\s+"([^"]+)"\s*:'
    foreach ($m in $rx.Matches($parserText)) {
        $labels[$m.Groups[1].Value] = $true
    }
    return $labels
}

function Get-SourceTokens([string]$text, [string]$pattern) {
    $set = @{}
    $rx = [regex]$pattern
    foreach ($m in $rx.Matches($text)) {
        $set[$m.Value] = $true
    }
    return $set
}

function Is-ReferencedLiteral([string]$text, [string]$token) {
    if ([string]::IsNullOrWhiteSpace($token)) { return $false }
    $esc = [regex]::Escape($token)
    return [regex]::IsMatch($text, "(`"|')" + $esc + "(`"|')")
}

function Count-LinesByPattern([string]$dir, [string]$pattern) {
    if (-not (Test-Path $dir)) { return @() }
    $files = Get-ChildItem -Path $dir -Recurse -Filter *.cs -File
    $hits = @()
    foreach ($f in $files) {
        $lines = Get-Content -Encoding UTF8 $f.FullName
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match $pattern) {
                $hits += [pscustomobject]@{
                    File = $f.FullName
                    Line = $i + 1
                    Text = $lines[$i].Trim()
                }
            }
        }
    }
    return $hits
}

function TopItems($map, [int]$n) {
    return $map.GetEnumerator() | Sort-Object -Property Value -Descending | Select-Object -First $n
}

function Count-PatternInFiles([string]$dir, [string]$pattern) {
    if (-not (Test-Path $dir)) { return 0 }
    $files = Get-ChildItem -Path $dir -Recurse -Filter *.cs -File
    $total = 0
    foreach ($f in $files) {
        $txt = Get-Content -Raw -Encoding UTF8 $f.FullName
        $total += ([regex]::Matches($txt, $pattern)).Count
    }
    return $total
}

if (-not $OutputReport) {
    $base = [System.IO.Path]::GetFileNameWithoutExtension($XmlPath)
    $OutputReport = "COBERTURA_XML_${base}.md"
}

$parserPath = Join-Path $PSScriptRoot "XpaParser.cs"
$writerPath = Join-Path $PSScriptRoot "ProjectGenerator.cs"

$parserText = Get-Text $parserPath
$writerText = Get-Text $writerPath
$allText = $parserText + "`n" + $writerText

[xml]$xmlDoc = Get-Content -Raw -Encoding UTF8 $XmlPath
$inv = Collect-XmlInventory $xmlDoc
$logicCaseLabels = Get-SwitchCaseLabels $parserText

# Model coverage by token inventory from source (better than raw literal lookup)
$modelTokenPattern = '\b(?:CTRL_[A-Z0-9_]+|FORM_[A-Z0-9_]+|FIELD(?:_[A-Z0-9_]+)?)\b'
$parserModelTokens = Get-SourceTokens $parserText $modelTokenPattern
$writerModelTokens = Get-SourceTokens $writerText $modelTokenPattern
$allModelTokens = @{}
foreach ($k in $parserModelTokens.Keys) { $allModelTokens[$k] = $true }
foreach ($k in $writerModelTokens.Keys) { $allModelTokens[$k] = $true }

$elementCoverage = @()
foreach ($kv in $inv.Elements.GetEnumerator() | Sort-Object Name) {
    $name = $kv.Key
    $cnt = $kv.Value
    $inParser = Is-ReferencedLiteral $parserText $name
    $inWriter = Is-ReferencedLiteral $writerText $name
    $elementCoverage += [pscustomobject]@{
        Name = $name
        Count = $cnt
        InParser = $inParser
        InWriter = $inWriter
    }
}

$modelCoverage = @()
foreach ($kv in $inv.Models.GetEnumerator() | Sort-Object Name) {
    $name = $kv.Key
    $cnt = $kv.Value
    $inParser = $parserModelTokens.ContainsKey($name)
    $inWriter = $writerModelTokens.ContainsKey($name)
    $modelCoverage += [pscustomobject]@{
        Name = $name
        Count = $cnt
        InParser = $inParser
        InWriter = $inWriter
    }
}

$logicCoverage = @()
foreach ($kv in $inv.LogicLineKinds.GetEnumerator() | Sort-Object Name) {
    $name = $kv.Key
    $cnt = $kv.Value
    $inSwitch = $logicCaseLabels.ContainsKey($name)
    $logicCoverage += [pscustomobject]@{
        Name = $name
        Count = $cnt
        ParsedBySwitch = $inSwitch
    }
}

$genExp = Count-PatternInFiles $GeneratedDir 'Exp_\d+\s*\('
$genCmd = Count-PatternInFiles $GeneratedDir '\bCustomCommand\b'
$genSilent = Count-PatternInFiles $GeneratedDir '\.SilentSet\s*\('
$genGapMarkers = Count-LinesByPattern $GeneratedDir '//.*(not mapped|skipped)'
$genGapCount = @($genGapMarkers).Count

$origExp = 0
$origCmd = 0
$origSilent = 0
if ($OriginalDir) {
    $origExp = Count-PatternInFiles $OriginalDir 'Exp_\d+\s*\('
    $origCmd = Count-PatternInFiles $OriginalDir '\bCustomCommand\b'
    $origSilent = Count-PatternInFiles $OriginalDir '\.SilentSet\s*\('
}

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine("# Cobertura XML -> Parser/Writer")
[void]$sb.AppendLine()
[void]$sb.AppendLine("- XML: " + $XmlPath)
[void]$sb.AppendLine("- Parser: " + $parserPath)
[void]$sb.AppendLine("- Writer: " + $writerPath)
[void]$sb.AppendLine("- Gerado: " + $GeneratedDir)
if ($OriginalDir) { [void]$sb.AppendLine("- Original: " + $OriginalDir) }
[void]$sb.AppendLine()

[void]$sb.AppendLine("## Inventario do XML")
[void]$sb.AppendLine()
[void]$sb.AppendLine("- Tokens de model detectados no Parser: " + $parserModelTokens.Count)
[void]$sb.AppendLine("- Tokens de model detectados no Writer: " + $writerModelTokens.Count)
[void]$sb.AppendLine("- Tokens de model unicos no conversor: " + $allModelTokens.Count)
[void]$sb.AppendLine()
[void]$sb.AppendLine("- Elementos distintos: " + $inv.Elements.Count)
[void]$sb.AppendLine("- Atributos distintos: " + $inv.Attributes.Count)
[void]$sb.AppendLine("- Models distintos (`PropertyList@model`): " + $inv.Models.Count)
[void]$sb.AppendLine("- Tipos de `LogicLine` distintos: " + $inv.LogicLineKinds.Count)
[void]$sb.AppendLine()

[void]$sb.AppendLine("### Top Elementos")
foreach ($it in TopItems $inv.Elements 30) {
    [void]$sb.AppendLine("- " + $it.Key + ": " + $it.Value)
}
[void]$sb.AppendLine()

[void]$sb.AppendLine("## Cobertura de LogicLine")
[void]$sb.AppendLine()
$missingLogic = $logicCoverage | Where-Object { -not $_.ParsedBySwitch } | Sort-Object Count -Descending
if ($missingLogic.Count -eq 0) {
    [void]$sb.AppendLine("Todos os tipos de `LogicLine` encontrados no XML possuem `case` no parser.")
} else {
    [void]$sb.AppendLine("Tipos de `LogicLine` sem `case` no parser:")
    foreach ($m in $missingLogic) {
        [void]$sb.AppendLine("- " + $m.Name + " (" + $m.Count + ")")
    }
}
[void]$sb.AppendLine()
[void]$sb.AppendLine("Observacao:")
[void]$sb.AppendLine("- `Remark` normalmente e nao funcional.")
[void]$sb.AppendLine()

[void]$sb.AppendLine("## Cobertura de Models")
[void]$sb.AppendLine()
$missingModelsParser = $modelCoverage | Where-Object { -not $_.InParser } | Sort-Object Count -Descending
$missingModelsWriter = $modelCoverage | Where-Object { -not $_.InWriter } | Sort-Object Count -Descending
[void]$sb.AppendLine("Sem referencia literal no Parser:")
foreach ($m in $missingModelsParser | Select-Object -First 50) {
    [void]$sb.AppendLine("- " + $m.Name + " (" + $m.Count + ")")
}
[void]$sb.AppendLine()
[void]$sb.AppendLine("Model tokens conhecidos no conversor:")
foreach ($k in ($allModelTokens.Keys | Sort-Object)) {
    [void]$sb.AppendLine("- " + $k)
}
[void]$sb.AppendLine()
[void]$sb.AppendLine("Sem referencia literal no Writer:")
foreach ($m in $missingModelsWriter | Select-Object -First 50) {
    [void]$sb.AppendLine("- " + $m.Name + " (" + $m.Count + ")")
}
[void]$sb.AppendLine()

[void]$sb.AppendLine("## Sinais semanticos Original vs Gerado")
[void]$sb.AppendLine()
[void]$sb.AppendLine("| Sinal | Original | Gerado |")
[void]$sb.AppendLine("|---|---:|---:|")
[void]$sb.AppendLine("| `Exp_#()` | " + $origExp + " | " + $genExp + " |")
[void]$sb.AppendLine("| `CustomCommand` | " + $origCmd + " | " + $genCmd + " |")
[void]$sb.AppendLine("| `.SilentSet(...)` | " + $origSilent + " | " + $genSilent + " |")
[void]$sb.AppendLine()

[void]$sb.AppendLine("## Marcadores de gap no Gerado")
[void]$sb.AppendLine()
[void]$sb.AppendLine("- Ocorrencias com not mapped|skipped: " + $genGapCount)
if ($genGapCount -gt 0) {
    [void]$sb.AppendLine()
    [void]$sb.AppendLine("| File | Linha | Trecho |")
    [void]$sb.AppendLine("|---|---:|---|")
    foreach ($h in (@($genGapMarkers) | Select-Object -First 120)) {
        $rel = $h.File
        if ($h.File.StartsWith((Get-Location).Path, [System.StringComparison]::OrdinalIgnoreCase)) {
            $rel = $h.File.Substring((Get-Location).Path.Length).TrimStart('\')
        }
        $txt = $h.Text.Replace("|", "\|")
        [void]$sb.AppendLine("| " + $rel + " | " + $h.Line + " | " + $txt + " |")
    }
}
[void]$sb.AppendLine()

[void]$sb.AppendLine("## Diagnostico de Prontidao")
[void]$sb.AppendLine()
$hasBlockingLogic = ($missingLogic | Where-Object { $_.Name -ne "Remark" -and $_.Count -gt 0 }).Count -gt 0
$hasBlockingMarkers = $genGapCount -gt 0
$nonBlockingWriterModels = @("FIELD", "FORM_GUI0", "CTRL_NONE")
$hasBlockingModels = ($missingModelsWriter | Where-Object { $_.Count -gt 0 -and ($nonBlockingWriterModels -notcontains $_.Name) }).Count -gt 0

if (-not $hasBlockingLogic -and -not $hasBlockingMarkers -and -not $hasBlockingModels) {
    [void]$sb.AppendLine("Status: PRONTO")
    [void]$sb.AppendLine("- Cobertura estrutural XML vs conversor sem bloqueadores evidentes.")
} else {
    [void]$sb.AppendLine("Status: NAO PRONTO")
    if ($hasBlockingLogic) { [void]$sb.AppendLine("- Existem tipos de `LogicLine` funcionais sem `case` no parser.") }
    if ($hasBlockingMarkers) { [void]$sb.AppendLine("- Existem marcadores not mapped/skipped no codigo gerado.") }
    if ($hasBlockingModels) { [void]$sb.AppendLine("- Existem `PropertyList@model` do XML sem cobertura de escrita no writer.") }
}
[void]$sb.AppendLine()

[void]$sb.AppendLine("## Proximos passos sugeridos")
[void]$sb.AppendLine()
[void]$sb.AppendLine("1. Tratar primeiro `LogicLine` sem `case` (impacto funcional direto).")
[void]$sb.AppendLine("2. Para cada `model` sem cobertura de writer, classificar: `UI`, `Printing`, `TextIO`, `Tree`, `Subform`.")
[void]$sb.AppendLine("3. Validar divergencias semanticas por pasta (`_4_TheMagicXpaEngine`, `_10_TreeControl`, ...).")
[void]$sb.AppendLine("4. Atualizar `DOCUMENTACAO_CONVERSOR_MAPA_XML_HEURISTICA.md` a cada nova regra heuristica.")

Set-Content -Path $OutputReport -Value $sb.ToString() -Encoding UTF8
Write-Output "Report written: $OutputReport"

