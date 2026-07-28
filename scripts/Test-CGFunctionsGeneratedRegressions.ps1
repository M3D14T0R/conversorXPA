[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$GeneratedProjectDirectory,

    [string]$ReportPath = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$resolved = [System.IO.Path]::GetFullPath($GeneratedProjectDirectory)
$projectDirectory = if (
    Test-Path -LiteralPath (Join-Path $resolved "CGFunctions.csproj") -PathType Leaf) {
    $resolved
}
else {
    $nested = Join-Path $resolved "CGFunctions"
    if (-not (Test-Path -LiteralPath (Join-Path $nested "CGFunctions.csproj") -PathType Leaf)) {
        throw "Projeto CGFunctions gerado não encontrado em: $resolved"
    }
    $nested
}

$successes = [System.Collections.Generic.List[object]]::new()
$failures = [System.Collections.Generic.List[object]]::new()

function Add-Result {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][bool]$Passed,
        [Parameter(Mandatory)][string]$Evidence
    )

    $result = [pscustomobject]@{
        Name = $Name
        Passed = $Passed
        Evidence = $Evidence
    }
    if ($Passed) {
        $successes.Add($result)
        Write-Host "[OK] $Name"
    }
    else {
        $failures.Add($result)
        Write-Host "[FALHOU] $Name - $Evidence" -ForegroundColor Red
    }
}

$messageControllerPath = Join-Path $projectDirectory "MensagemCIGAM_MensagemCigam.cs"
$messageControllerText = if (Test-Path -LiteralPath $messageControllerPath -PathType Leaf) {
    [System.IO.File]::ReadAllText($messageControllerPath)
}
else {
    ""
}

foreach ($viewName in @("MensagemCigamDialogoByExp", "MensagemCigamDialogo11")) {
    $viewPath = Join-Path $projectDirectory "Views\$viewName.cs"
    if (-not (Test-Path -LiteralPath $viewPath -PathType Leaf)) {
        Add-Result `
            -Name "$viewName foi gerado" `
            -Passed $false `
            -Evidence "Arquivo ausente: $viewPath"
        continue
    }

    $viewText = [System.IO.File]::ReadAllText($viewPath)
    foreach ($button in @(
            [pscustomobject]@{ Control = "btnBOtao1"; Column = "b_otao1" },
            [pscustomobject]@{ Control = "btnBOtao2"; Column = "b_otao2" },
            [pscustomobject]@{ Control = "btnBOtao3"; Column = "b_otao3" },
            [pscustomobject]@{ Control = "btnBDetalhes"; Column = "b_detalhes" }
        )) {
        $expected = "$($button.Control).Data = (XPARuntimeCore.Box.UI.Advanced.ButtonData)_controller.$($button.Column);"
        Add-Result `
            -Name "$viewName preserva a identidade XPA de $($button.Column)" `
            -Passed $viewText.Contains($expected) `
            -Evidence "$viewPath`: esperado '$expected'"
    }

    Add-Result `
        -Name "$viewName não substitui botões XPA por ButtonData calculado" `
        -Passed (-not $viewText.Contains("new XPARuntimeCore.Box.UI.Advanced.ButtonData(() =>")) `
        -Evidence "$viewPath contém ButtonData calculado"
}

foreach ($handler in @(
        [pscustomobject]@{ Column = "b_otao1"; Command = "Botao_1" },
        [pscustomobject]@{ Column = "b_otao2"; Command = "Botao_2" },
        [pscustomobject]@{ Column = "b_otao3"; Command = "Botao_3" },
        [pscustomobject]@{ Column = "b_detalhes"; Command = "Botao_Detalhes" }
    )) {
    $expandHandler = "Handlers.Add(Command.Expand, `"$($handler.Column)`", HandlerScope.CurrentTaskOnly)"
    $invokeHandler = "Invoke($($handler.Command));"
    Add-Result `
        -Name "Mensagem CIGAM conecta $($handler.Column) ao evento $($handler.Command)" `
        -Passed ($messageControllerText.Contains($expandHandler) -and
            $messageControllerText.Contains($invokeHandler)) `
        -Evidence "$messageControllerPath`: esperado '$expandHandler' e '$invokeHandler'"
}

$report = [pscustomobject]@{
    GeneratedProjectDirectory = $projectDirectory
    ExecutedAt = (Get-Date).ToString("o")
    Passed = $failures.Count -eq 0
    SuccessCount = $successes.Count
    FailureCount = $failures.Count
    Successes = @($successes)
    Failures = @($failures)
}

if (-not [string]::IsNullOrWhiteSpace($ReportPath)) {
    $resolvedReportPath = [System.IO.Path]::GetFullPath($ReportPath)
    $reportDirectory = Split-Path -Parent $resolvedReportPath
    if (-not [string]::IsNullOrWhiteSpace($reportDirectory)) {
        New-Item -ItemType Directory -Force -Path $reportDirectory | Out-Null
    }
    $report | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath $resolvedReportPath -Encoding UTF8
}

if ($failures.Count -gt 0) {
    throw "$($failures.Count) regressão(ões) encontrada(s) no CGFunctions gerado."
}

Write-Host "$($successes.Count) testes de regressão do CGFunctions passaram."
