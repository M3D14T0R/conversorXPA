[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$GeneratedProjectDirectory,
    [string]$ReportPath = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$resolved = [System.IO.Path]::GetFullPath($GeneratedProjectDirectory)
$projectDirectory = if (Test-Path -LiteralPath (Join-Path $resolved "CGLFCore.csproj") -PathType Leaf) {
    $resolved
}
else {
    Join-Path $resolved "CGLFCore"
}
$sourcePath = Join-Path $projectDirectory "KF00013_CfgTOV2SetaConfigMemoria.cs"
$checks = [System.Collections.Generic.List[object]]::new()

function Add-Check {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][bool]$Passed,
        [Parameter(Mandatory)][string]$Evidence
    )

    $checks.Add([pscustomobject]@{
        Name = $Name
        Passed = $Passed
        Evidence = $Evidence
    })
    Write-Host "$(if ($Passed) { '[OK]' } else { '[FALHOU]' }) $Name"
}

if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
    Add-Check `
        -Name "KF00013 gerado" `
        -Passed $false `
        -Evidence "Arquivo ausente: $sourcePath"
}
else {
    $source = [System.IO.File]::ReadAllText($sourcePath)
    Add-Check `
        -Name "DSOURCE 191,1 resolve configuracao tipo operacao" `
        -Passed ($source.IndexOf(
            "typeof(CGData.Models.CONFIGURACAO_TIPO_OPERACAO__GECONFIGTIPOOPER_)",
            [System.StringComparison]::Ordinal) -ge 0) `
        -Evidence $sourcePath
    Add-Check `
        -Name "DSOURCE 192,1 resolve dado config tipo operacao" `
        -Passed ($source.IndexOf(
            "typeof(CGData.Models.DADO_CONFIG_TIPO_OPERACAO)",
            [System.StringComparison]::Ordinal) -ge 0) `
        -Evidence $sourcePath
    Add-Check `
        -Name "KF00013 nao regride DSOURCE para MODULO" `
        -Passed ($source.IndexOf(
            "typeof(CGData.Models.MODULO)",
            [System.StringComparison]::Ordinal) -lt 0) `
        -Evidence $sourcePath
    Add-Check `
        -Name "KF00013 nao regride DSOURCE para FUNCIONALIDADE" `
        -Passed ($source.IndexOf(
            "typeof(CGData.Models.FUNCIONALIDADE)",
            [System.StringComparison]::Ordinal) -lt 0) `
        -Evidence $sourcePath
}

$failures = @($checks | Where-Object { -not $_.Passed })
$report = [pscustomobject]@{
    GeneratedProjectDirectory = $projectDirectory
    ExecutedAt = (Get-Date).ToString("o")
    Passed = $failures.Count -eq 0
    Checks = @($checks)
}

if (-not [string]::IsNullOrWhiteSpace($ReportPath)) {
    $resolvedReport = [System.IO.Path]::GetFullPath($ReportPath)
    $reportDirectory = Split-Path -Parent $resolvedReport
    if (-not [string]::IsNullOrWhiteSpace($reportDirectory)) {
        New-Item -ItemType Directory -Force -Path $reportDirectory | Out-Null
    }
    $report | ConvertTo-Json -Depth 6 |
        Set-Content -LiteralPath $resolvedReport -Encoding UTF8
}

if ($failures.Count -gt 0) {
    throw "$($failures.Count) regressao(oes) encontrada(s) no CGLFCore gerado."
}

Write-Host "$($checks.Count) testes de regressao do CGLFCore passaram."
