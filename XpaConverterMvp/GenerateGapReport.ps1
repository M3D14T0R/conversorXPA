param(
    [Parameter(Mandatory=$true)][string]$XmlPath,
    [Parameter(Mandatory=$true)][string]$GeneratedDir,
    [Parameter(Mandatory=$false)][string]$OriginalDir = "",
    [Parameter(Mandatory=$false)][int]$MaxDetailRows = 200,
    [Parameter(Mandatory=$false)][string]$OutputReport = ""
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $XmlPath)) {
    throw "XML not found: $XmlPath"
}
if (-not (Test-Path $GeneratedDir)) {
    throw "Generated dir not found: $GeneratedDir"
}

if ([string]::IsNullOrWhiteSpace($OutputReport)) {
    $base = [System.IO.Path]::GetFileNameWithoutExtension($XmlPath)
    $OutputReport = "RELATORIO_GAPS_${base}.md"
}

$parserPath = Join-Path $PSScriptRoot "XpaParser.cs"
$writerPath = Join-Path $PSScriptRoot "ProjectGenerator.cs"

if (-not (Test-Path $parserPath)) { throw "Parser not found: $parserPath" }
if (-not (Test-Path $writerPath)) { throw "Writer not found: $writerPath" }

$parserText = Get-Content -Raw -Encoding UTF8 $parserPath
$writerText = Get-Content -Raw -Encoding UTF8 $writerPath
$originalText = ""
if (-not [string]::IsNullOrWhiteSpace($OriginalDir) -and (Test-Path $OriginalDir)) {
    $origFiles = Get-ChildItem -Path $OriginalDir -Recurse -Filter *.cs -File -ErrorAction SilentlyContinue
    if ($origFiles) {
        $originalText = ($origFiles | ForEach-Object { Get-Content -Raw -Encoding UTF8 $_.FullName }) -join "`n"
    }
}

function Add-Count([hashtable]$map, [string]$key) {
    if ([string]::IsNullOrWhiteSpace($key)) { return }
    if ($map.ContainsKey($key)) { $map[$key]++ } else { $map[$key] = 1 }
}

function Is-ReferencedLiteral([string]$text, [string]$token) {
    if ([string]::IsNullOrWhiteSpace($token)) { return $false }
    $esc = [regex]::Escape($token)
    return [regex]::IsMatch($text, "(`"|')" + $esc + "(`"|')")
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
            if ($child.NodeType -eq [System.Xml.XmlNodeType]::Element) {
                $first = $child
                break
            }
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

function Get-SwitchCaseLabels([string]$text) {
    $labels = @{}
    $rx = [regex]'case\s+"([^"]+)"\s*:'
    foreach ($m in $rx.Matches($text)) {
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

function IsLikelyStructuralElement([string]$name) {
    if ([string]::IsNullOrWhiteSpace($name)) { return $false }
    if ($name.StartsWith("_")) { return $true }
    $exact = @(
        "Task","TaskLogic","TaskLogicUnit","TaskLogicUnits","LogicUnit","LogicLine","LogicLines",
        "Form","Forms","Control","Controls","PropertyList","Properties",
        "RaiseEvent","Event","EventType","InternalEventID","KeyCombinationID","PublicObject",
        "Arguments","Argument","MenusRepository","Menu","MenuEntry","MenuType",
        "Data","DataSource","Models","Model","ModelRef","Type","Types","Expression","ExpressionId",
        "MainProgram","Programs","Program","Components","Component"
    )
    if ($exact -contains $name) { return $true }
    $keywords = @("Repository","Logic","Handler","Subform","ContextMenu","PulldownMenu","TextIO","Print")
    foreach ($k in $keywords) {
        if ($name -like "*$k*") { return $true }
    }
    return $false
}

function IsLikelyStructuralAttribute([string]$name) {
    if ([string]::IsNullOrWhiteSpace($name)) { return $false }
    $known = @(
        "val","obj","id","model","line","Exp","exp","ditidx","type","skip","name","key","event",
        "objref","publicobject","internaleventid","keycombinationid","isn_father"
    )
    if ($known -contains $name.ToLowerInvariant()) { return $true }
    if ($name -match '^[A-Z][A-Za-z0-9_]{1,30}$') { return $true }
    return $false
}

function IsKnownLiteralFalsePositiveElement([string]$name) {
    if ([string]::IsNullOrWhiteSpace($name)) { return $false }
    $known = @(
        "Form"
    )
    return $known -contains $name
}

function IsKnownLiteralFalsePositiveAttribute([string]$name) {
    if ([string]::IsNullOrWhiteSpace($name)) { return $false }
    $n = $name.ToLowerInvariant()
    $known = @(
        "date",
        "time",
        "del",
        "parent"
    )
    return $known -contains $n
}

function IsActionableParserOnlyElement([string]$name) {
    if ([string]::IsNullOrWhiteSpace($name)) { return $false }
    # This category is only useful for explicit writer-model contracts.
    # Generic XML structural names are intentionally ignored here.
    return $false
}

function IsActionableParserOnlyAttribute([string]$name) {
    if ([string]::IsNullOrWhiteSpace($name)) { return $false }
    # Same rationale as elements: avoid noisy literal checks.
    return $false
}

# 1) GAPs de geracao (linhas GAP no codigo gerado)
$generationRows = @()
$viewExpIntegrityRows = @()
$viewExpRefsCount = 0
$viewExpMissingCount = 0
$files = Get-ChildItem -Path $GeneratedDir -Recurse -Filter *.cs -File
foreach ($f in $files) {
    $lines = Get-Content -Encoding UTF8 $f.FullName
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        if ($line -match "GAP:") {
            $msg = $line.Trim()
            $type = "GenerationUnknown"
            if ($msg -match "View event binding") { $type = "ViewEventBinding" }
            elseif ($msg -match "View data binding") { $type = "ViewDataBinding" }
            elseif ($msg -match "Print data binding") { $type = "PrintDataBinding" }
            elseif ($msg -match "TextIO data binding") { $type = "TextIODataBinding" }
            elseif ($msg -match "control model") { $type = "ControlModelMapping" }
            elseif ($msg -match "RowAction") { $type = "RowActionMapping" }
            $generationRows += [pscustomobject]@{
                Category = "Geracao"
                File = $f.FullName
                Line = $i + 1
                Type = $type
                Message = $msg
            }
        }
    }
}

# 1.1) GAP critico: View referencia _controller.Exp_N(), mas controller nao emite Exp_N()
$viewCodeFiles = Get-ChildItem -Path $GeneratedDir -Recurse -Filter *View.cs -File -ErrorAction SilentlyContinue
foreach ($vf in $viewCodeFiles) {
    $viewText = Get-Content -Raw -Encoding UTF8 $vf.FullName
    $controllerMatch = [regex]::Match($viewText, 'readonly\s+([A-Za-z_][A-Za-z0-9_\.]*)\s+_controller\s*;')
    if (-not $controllerMatch.Success) { continue }
    $controllerType = ($controllerMatch.Groups[1].Value -split '\.')[-1]
    if ([string]::IsNullOrWhiteSpace($controllerType)) { continue }

    $controllerFile = Get-ChildItem -Path $GeneratedDir -Recurse -Filter "$controllerType.generated.cs" -File -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $controllerFile) { continue }
    $controllerText = Get-Content -Raw -Encoding UTF8 $controllerFile.FullName

    $expRefs = [regex]::Matches($viewText, '_controller\.Exp_(\d+)\s*\(\s*\)')
    $viewExpRefsCount += $expRefs.Count
    $missingIds = New-Object System.Collections.Generic.HashSet[string]
    foreach ($m in $expRefs) {
        $id = $m.Groups[1].Value
        if (-not [regex]::IsMatch($controllerText, "\bExp_$id\s*\(")) {
            [void]$missingIds.Add($id)
        }
    }

    foreach ($id in $missingIds) {
        $viewExpMissingCount++
        $viewExpIntegrityRows += [pscustomobject]@{
            ViewFile = $vf.FullName
            ControllerFile = $controllerFile.FullName
            ExpressionId = $id
        }
        $generationRows += [pscustomobject]@{
            Category = "Geracao"
            File = $vf.FullName
            Line = 0
            Type = "ExpressionBindingMethodMissing"
            Message = "// GAP: CRITICAL - View referencia _controller.Exp_$id(), mas $controllerType.generated.cs nao emite Exp_$id()."
        }
    }
}

# 2) GAPs de mapeamento XML (inventario vs parser/writer)
[xml]$xmlDoc = Get-Content -Raw -Encoding UTF8 $XmlPath
$inv = Collect-XmlInventory $xmlDoc
$logicCases = Get-SwitchCaseLabels $parserText

$modelTokenPattern = '\b(?:CTRL_[A-Z0-9_]+|FORM_[A-Z0-9_]+|FIELD(?:_[A-Z0-9_]+)?)\b'
$writerModelTokens = Get-SourceTokens $writerText $modelTokenPattern

$mappingRows = @()
$inventoryOnlyRows = @()
$coveredByOriginalRows = @()
$parsedNotEmittedRows = @()

# 2.1 LogicLine no XML sem case no parser
foreach ($kv in $inv.LogicLineKinds.GetEnumerator() | Sort-Object Key) {
    if (-not $logicCases.ContainsKey($kv.Key)) {
        $mappingRows += [pscustomobject]@{
            Category = "MapeamentoXML"
            File = $XmlPath
            Line = 0
            Type = "LogicLineNoParserCase"
            Message = "LogicLine '$($kv.Key)' sem case no parser (ocorrencias: $($kv.Value))."
        }
    }
}

# 2.2 Model em PropertyList sem token de writer
foreach ($kv in $inv.Models.GetEnumerator() | Sort-Object Key) {
    if (-not $writerModelTokens.ContainsKey($kv.Key)) {
        $mappingRows += [pscustomobject]@{
            Category = "MapeamentoXML"
            File = $XmlPath
            Line = 0
            Type = "PropertyListModelNoWriterToken"
            Message = "PropertyList@model '$($kv.Key)' sem token no writer (ocorrencias: $($kv.Value))."
        }
    }
}

# 2.3 Elemento XML sem referencia literal em parser e writer
foreach ($kv in $inv.Elements.GetEnumerator() | Sort-Object Key) {
    $inParser = Is-ReferencedLiteral $parserText $kv.Key
    $inWriter = Is-ReferencedLiteral $writerText $kv.Key
    if ($inParser -and -not $inWriter -and (IsLikelyStructuralElement $kv.Key) -and (IsActionableParserOnlyElement $kv.Key)) {
        $parsedNotEmittedRows += [pscustomobject]@{
            Category = "ParseadoNoParserSemEmissao"
            File = $XmlPath
            Line = 0
            Type = "XmlElementParserOnly"
            Message = "Elemento XML '$($kv.Key)' referenciado no parser e sem referencia literal no writer (ocorrencias: $($kv.Value))."
        }
    }
    if (-not $inParser -and -not $inWriter) {
        $row = [pscustomobject]@{
            Category = "MapeamentoXML"
            File = $XmlPath
            Line = 0
            Type = "XmlElementNoReference"
            Message = "Elemento XML '$($kv.Key)' sem referencia literal no parser/writer (ocorrencias: $($kv.Value))."
        }
        if (IsKnownLiteralFalsePositiveElement $kv.Key) {
            $inventoryOnlyRows += $row
            continue
        }
        $inOriginal = (-not [string]::IsNullOrWhiteSpace($originalText)) -and (Is-ReferencedLiteral $originalText $kv.Key)
        if ($inOriginal) { $coveredByOriginalRows += $row }
        elseif (IsLikelyStructuralElement $kv.Key) { $mappingRows += $row }
        else { $inventoryOnlyRows += $row }
    }
}

# 2.4 Atributo XML sem referencia literal em parser e writer
foreach ($kv in $inv.Attributes.GetEnumerator() | Sort-Object Key) {
    $inParser = Is-ReferencedLiteral $parserText $kv.Key
    $inWriter = Is-ReferencedLiteral $writerText $kv.Key
    if ($inParser -and -not $inWriter -and (IsLikelyStructuralAttribute $kv.Key) -and (IsActionableParserOnlyAttribute $kv.Key)) {
        $parsedNotEmittedRows += [pscustomobject]@{
            Category = "ParseadoNoParserSemEmissao"
            File = $XmlPath
            Line = 0
            Type = "XmlAttributeParserOnly"
            Message = "Atributo XML '$($kv.Key)' referenciado no parser e sem referencia literal no writer (ocorrencias: $($kv.Value))."
        }
    }
    if (-not $inParser -and -not $inWriter) {
        $row = [pscustomobject]@{
            Category = "MapeamentoXML"
            File = $XmlPath
            Line = 0
            Type = "XmlAttributeNoReference"
            Message = "Atributo XML '$($kv.Key)' sem referencia literal no parser/writer (ocorrencias: $($kv.Value))."
        }
        if (IsKnownLiteralFalsePositiveAttribute $kv.Key) {
            $inventoryOnlyRows += $row
            continue
        }
        $inOriginal = (-not [string]::IsNullOrWhiteSpace($originalText)) -and (Is-ReferencedLiteral $originalText $kv.Key)
        if ($inOriginal) { $coveredByOriginalRows += $row }
        elseif (IsLikelyStructuralAttribute $kv.Key) { $mappingRows += $row }
        else { $inventoryOnlyRows += $row }
    }
}

$nonBlockingWriterModels = @("FIELD", "FORM_GUI0", "CTRL_NONE")

# 2.5 Model parseado no parser e sem evidencia de writer
foreach ($kv in $inv.Models.GetEnumerator() | Sort-Object Key) {
    $inParser = Is-ReferencedLiteral $parserText $kv.Key
    $inWriter = Is-ReferencedLiteral $writerText $kv.Key
    if ($inParser -and -not $inWriter -and ($nonBlockingWriterModels -notcontains $kv.Key)) {
        $parsedNotEmittedRows += [pscustomobject]@{
            Category = "ParseadoNoParserSemEmissao"
            File = $XmlPath
            Line = 0
            Type = "PropertyListModelParserOnly"
            Message = "PropertyList@model '$($kv.Key)' referenciado no parser e sem token literal no writer (ocorrencias: $($kv.Value))."
        }
    }
}

$allRows = @($generationRows + $mappingRows)

$totalGeneration = @($generationRows).Count
$totalMapping = @($mappingRows).Count
$totalParsedNotEmitted = @($parsedNotEmittedRows).Count
$totalInventoryOnly = @($inventoryOnlyRows).Count
$totalCoveredByOriginal = @($coveredByOriginalRows).Count
$total = @($allRows).Count

$byCategory = $allRows | Group-Object Category | Sort-Object Count -Descending
$byType = $allRows | Group-Object Type | Sort-Object Count -Descending
$byFile = $allRows | Group-Object File | Sort-Object Count -Descending

$now = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine("# Relatorio de GAPs")
[void]$sb.AppendLine()
[void]$sb.AppendLine("Data: $now")
[void]$sb.AppendLine("XML: $XmlPath")
[void]$sb.AppendLine("Base gerada: $GeneratedDir")
[void]$sb.AppendLine("Base original: " + ($(if ([string]::IsNullOrWhiteSpace($OriginalDir)) {"(nao informada)"} else {$OriginalDir})))
[void]$sb.AppendLine("Total de GAPs: **$total**")
[void]$sb.AppendLine("- GAPs de geracao: **$totalGeneration**")
[void]$sb.AppendLine("- GAPs de mapeamento XML (acionaveis): **$totalMapping**")
[void]$sb.AppendLine("- Parseado no parser sem emissao (informativo): **$totalParsedNotEmitted**")
[void]$sb.AppendLine("- Itens cobertos indiretamente (encontrados no original): **$totalCoveredByOriginal**")
[void]$sb.AppendLine("- Itens de inventario XML nao contabilizados como GAP acionavel: **$totalInventoryOnly**")
[void]$sb.AppendLine()

[void]$sb.AppendLine("## Integridade de Exp (View -> Controller)")
[void]$sb.AppendLine()
[void]$sb.AppendLine("- Referencias `_controller.Exp_N()` em `*View.cs`: **$viewExpRefsCount**")
[void]$sb.AppendLine("- Referencias sem metodo correspondente no controller: **$viewExpMissingCount**")
[void]$sb.AppendLine()

[void]$sb.AppendLine("## GAPs por categoria")
[void]$sb.AppendLine()
[void]$sb.AppendLine("| Categoria | Qtde |")
[void]$sb.AppendLine("|---|---:|")
foreach ($g in $byCategory) {
    [void]$sb.AppendLine("| $($g.Name) | $($g.Count) |")
}
[void]$sb.AppendLine()

[void]$sb.AppendLine("## GAPs por tipo")
[void]$sb.AppendLine()
[void]$sb.AppendLine("| Tipo | Qtde |")
[void]$sb.AppendLine("|---|---:|")
foreach ($g in $byType) {
    [void]$sb.AppendLine("| $($g.Name) | $($g.Count) |")
}
[void]$sb.AppendLine()

[void]$sb.AppendLine("## GAPs por arquivo")
[void]$sb.AppendLine()
[void]$sb.AppendLine("| Arquivo | Qtde |")
[void]$sb.AppendLine("|---|---:|")
foreach ($g in $byFile | Select-Object -First 200) {
    [void]$sb.AppendLine("| $($g.Name) | $($g.Count) |")
}
[void]$sb.AppendLine()

[void]$sb.AppendLine("## Detalhe dos GAPs de geracao")
[void]$sb.AppendLine()
[void]$sb.AppendLine("| Arquivo | Linha | Tipo | Mensagem |")
[void]$sb.AppendLine("|---|---:|---|---|")
foreach ($r in $generationRows | Select-Object -First 400) {
    $msgEsc = $r.Message -replace '\|','\\|'
    [void]$sb.AppendLine("| $($r.File) | $($r.Line) | $($r.Type) | $msgEsc |")
}
[void]$sb.AppendLine()

[void]$sb.AppendLine("## Detalhe: Integridade de Exp (View -> Controller)")
[void]$sb.AppendLine()
[void]$sb.AppendLine("| View | Controller | Exp |")
[void]$sb.AppendLine("|---|---|---:|")
foreach ($r in $viewExpIntegrityRows | Select-Object -First $MaxDetailRows) {
    [void]$sb.AppendLine("| $($r.ViewFile) | $($r.ControllerFile) | $($r.ExpressionId) |")
}
[void]$sb.AppendLine()

[void]$sb.AppendLine("## Detalhe dos GAPs de mapeamento XML")
[void]$sb.AppendLine()
[void]$sb.AppendLine("| Origem | Tipo | Mensagem |")
[void]$sb.AppendLine("|---|---|---|")
foreach ($r in $mappingRows | Select-Object -First $MaxDetailRows) {
    $msgEsc = $r.Message -replace '\|','\\|'
    [void]$sb.AppendLine("| $($r.File) | $($r.Type) | $msgEsc |")
}

[void]$sb.AppendLine()
[void]$sb.AppendLine("## Detalhe: Parseado no parser sem emissao")
[void]$sb.AppendLine()
[void]$sb.AppendLine("| Origem | Tipo | Mensagem |")
[void]$sb.AppendLine("|---|---|---|")
foreach ($r in $parsedNotEmittedRows | Select-Object -First $MaxDetailRows) {
    $msgEsc = $r.Message -replace '\|','\\|'
    [void]$sb.AppendLine("| $($r.File) | $($r.Type) | $msgEsc |")
}

[void]$sb.AppendLine()
[void]$sb.AppendLine("## Itens de inventario XML (informativo, baixo impacto)")
[void]$sb.AppendLine()
[void]$sb.AppendLine("| Origem | Tipo | Mensagem |")
[void]$sb.AppendLine("|---|---|---|")
foreach ($r in $inventoryOnlyRows | Select-Object -First $MaxDetailRows) {
    $msgEsc = $r.Message -replace '\|','\\|'
    [void]$sb.AppendLine("| $($r.File) | $($r.Type) | $msgEsc |")
}

[void]$sb.AppendLine()
[void]$sb.AppendLine("## Itens cobertos indiretamente (presentes no original)")
[void]$sb.AppendLine()
[void]$sb.AppendLine("| Origem | Tipo | Mensagem |")
[void]$sb.AppendLine("|---|---|---|")
foreach ($r in $coveredByOriginalRows | Select-Object -First $MaxDetailRows) {
    $msgEsc = $r.Message -replace '\|','\\|'
    [void]$sb.AppendLine("| $($r.File) | $($r.Type) | $msgEsc |")
}

Set-Content -Path $OutputReport -Value $sb.ToString() -Encoding UTF8
Write-Output "Report generated: $OutputReport (generation=$totalGeneration, mapping_actionable=$totalMapping, covered_by_original=$totalCoveredByOriginal, inventory_only=$totalInventoryOnly, total=$total)"

