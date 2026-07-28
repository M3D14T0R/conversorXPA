[CmdletBinding()]
param(
    [string]$SlicesRoot = "E:\probes\20260725-112205-CGGeralPipeline\slices",
    [string[]]$Slice = @(),
    [switch]$SkipRuntimeSmoke
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$failures = [System.Collections.Generic.List[string]]::new()
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
    if (-not $Passed) {
        $failures.Add("$Name - $Evidence")
    }
}

function Assert-FileMatch {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Pattern,
        [switch]$Not
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Add-Check -Name $Name -Passed $false -Evidence "Arquivo ausente: $Path"
        return
    }

    $content = Get-Content -LiteralPath $Path -Raw
    $matched = [regex]::IsMatch($content, $Pattern)
    $passed = if ($Not) { -not $matched } else { $matched }
    Add-Check -Name $Name -Passed $passed -Evidence $Path
}

function Get-ProjectRoot {
    param([Parameter(Mandatory)][string]$Name)
    return Join-Path $SlicesRoot "$Name\projects\28-CGGeral\CGGeral"
}

function Read-AppendedText {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][long]$Offset
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return ""
    }

    $stream = [System.IO.File]::Open(
        $Path,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::ReadWrite)
    try {
        if ($Offset -gt $stream.Length) {
            $Offset = 0
        }
        [void]$stream.Seek($Offset, [System.IO.SeekOrigin]::Begin)
        $reader = [System.IO.StreamReader]::new(
            $stream,
            [System.Text.Encoding]::UTF8,
            $true,
            4096,
            $true)
        try {
            return $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Assert-CG06301RuntimeSmoke {
    $name = "CG06301 subform refresh settles at runtime"
    $root = Get-ProjectRoot "CG06301"
    $applicationDirectory = Join-Path $root "bin\Release\net472"
    $profilePath = Join-Path $SlicesRoot "CG06301\XpaRuntime.GenericLauncher.CG06301.ini"
    $logPath = Join-Path $applicationDirectory "diagnostics\cg06301-controller.log"
    $launcher = Join-Path (Split-Path -Parent $PSScriptRoot) "XpaRuntime.GenericLauncher\bin\Release\net472\XpaRuntime.GenericLauncher.exe"

    foreach ($requiredFile in @($launcher, $profilePath)) {
        if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
            Add-Check -Name $name -Passed $false -Evidence "Arquivo ausente: $requiredFile"
            return
        }
    }

    $initialOffset = if (Test-Path -LiteralPath $logPath -PathType Leaf) {
        (Get-Item -LiteralPath $logPath).Length
    }
    else {
        0L
    }

    $process = $null
    try {
        $process = Start-Process `
            -FilePath $launcher `
            -ArgumentList @("--ini", "`"$profilePath`"") `
            -WorkingDirectory (Split-Path -Parent $launcher) `
            -WindowStyle Hidden `
            -PassThru

        Start-Sleep -Seconds 12
        $firstText = Read-AppendedText -Path $logPath -Offset $initialOffset
        $settledOffset = if (Test-Path -LiteralPath $logPath -PathType Leaf) {
            (Get-Item -LiteralPath $logPath).Length
        }
        else {
            0L
        }

        Start-Sleep -Seconds 5
        $settledText = Read-AppendedText -Path $logPath -Offset $settledOffset
        $attached = [regex]::IsMatch(
            $firstText,
            'CGGeral\.CG06301_CadastroDeEngenharia.*event=TaskAttached')
        $refreshAfterSettling = [regex]::Matches(
            $settledText,
            'ProcessingCommand.*RefreshSubForm').Count
        $responsive = -not $process.HasExited -and $process.Responding
        $passed = $attached -and $responsive -and $refreshAfterSettling -eq 0
        Add-Check `
            -Name $name `
            -Passed $passed `
            -Evidence "attached=$attached responsive=$responsive refreshAfterSettling=$refreshAfterSettling log=$logPath"
    }
    catch {
        Add-Check -Name $name -Passed $false -Evidence $_.Exception.Message
    }
    finally {
        if ($null -ne $process -and -not $process.HasExited) {
            Stop-Process -Id $process.Id
        }
    }
}

$available = @(
    "CG00347",
    "CG00341",
    "CG00343",
    "CG00344",
    "CG00374",
    "CG02075",
    "CG02103",
    "CG02109",
    "CG06301",
    "CG06388",
    "CG06353Flow"
)
$selected = if ($Slice.Count -eq 0) { $available } else { @($Slice) }

foreach ($name in $selected) {
    $projectRoot = Get-ProjectRoot -Name $name
    $projectFile = Join-Path $projectRoot "CGGeral.csproj"
    if (-not (Test-Path -LiteralPath $projectFile -PathType Leaf)) {
        Add-Check -Name "$name project" -Passed $false -Evidence "Projeto ausente: $projectFile"
        continue
    }

    $buildOutput = & dotnet build $projectFile -c Release --no-restore 2>&1
    Add-Check `
        -Name "$name Release build" `
        -Passed ($LASTEXITCODE -eq 0) `
        -Evidence (($buildOutput | Select-Object -Last 1) -join "")
}

if ($selected -contains "CG00347") {
    $root = Get-ProjectRoot "CG00347"
    Assert-FileMatch `
        -Name "CG00347 Sair caption" `
        -Path (Join-Path $root "Views\CentroDeArmazenagemCentroDeArmazenagem.Designer.cs") `
        -Pattern 'btnBSair\.Text\s*=\s*"Sai&r"'
}

if ($selected -contains "CG00341") {
    $root = Get-ProjectRoot "CG00341"
    $designer = Join-Path $root "Views\_CadastroDeMateriaisMateriaisNOVO.Designer.cs"
    Assert-FileMatch `
        -Name "CG00341 new-material form generated" `
        -Path $designer `
        -Pattern 'partial class _CadastroDeMateriaisMateriaisNOVO'
    Assert-FileMatch `
        -Name "CG00341 tab control generated" `
        -Path $designer `
        -Pattern 'Controls\.Add\(tabGUIAVTabela\);'
    Assert-FileMatch `
        -Name "CG00341 tab remains behind page controls" `
        -Path $designer `
        -Pattern 'tabGUIAVTabela\.SendToBack\(\);'
    Assert-FileMatch `
        -Name "CG00341 reapplies tab z-order after load" `
        -Path (Join-Path $root "Views\_CadastroDeMateriaisMateriaisNOVO.cs") `
        -Pattern 'Shown\s*\+=\s*\(_,\s*_\)\s*=>[\s\S]*tabGUIAVTabela\.SendToBack\(\);'
    Assert-FileMatch `
        -Name "CG00341 keeps left menu background behind its labels" `
        -Path $designer `
        -Pattern 'lbl9223\.SendToBack\(\);'
    Assert-FileMatch `
        -Name "CG00341 reapplies left menu background z-order after load" `
        -Path (Join-Path $root "Views\_CadastroDeMateriaisMateriaisNOVO.cs") `
        -Pattern 'Shown\s*\+=\s*\(_,\s*_\)\s*=>[\s\S]*lbl9223\.SendToBack\(\);'
    Assert-FileMatch `
        -Name "CG00341 PCP child inherits tab page" `
        -Path $designer `
        -Pattern 'cboVTipoDePlanejamento\.BoundTo\s*=\s*new XPARuntimeCore\.Box\.UI\.ControlBinding\(tabGUIAVTabela,\s*2\);'
    Assert-FileMatch `
        -Name "CG00341 serial-number container inherits tab page" `
        -Path $designer `
        -Pattern 'lblExigir_aInforma_o\.BoundTo\s*=\s*new XPARuntimeCore\.Box\.UI\.ControlBinding\(tabGUIAVTabela,\s*6\);'
    Assert-FileMatch `
        -Name "CG00341 material query included" `
        -Path (Join-Path $root "CG00374_ConsultaMateriais.cs") `
        -Pattern 'class\s+CG00374_ConsultaMateriais'
}

if ($selected -contains "CG00343") {
    $root = Get-ProjectRoot "CG00343"
    Assert-FileMatch `
        -Name "CG00343 calls typed CGLFCore configuration task" `
        -Path (Join-Path $root "CG00343_Movimentos_343.cs") `
        -Pattern 'CGLFCore\.KF00003_CfgTO_V2_BuscaConfigsTO'
    Assert-FileMatch `
        -Name "CG00343 launcher supplies the CIGAM Wiki base URL" `
        -Path (Join-Path $SlicesRoot "CG00343\XpaRuntime.GenericLauncher.CG00343.ini") `
        -Pattern '(?m)^CFG_3179=https://www\.cigam\.com\.br/wiki/index\.php$'
}

if ($selected -contains "CG00344") {
    $root = Get-ProjectRoot "CG00344"
    $controller = Join-Path $root "CG00344_Grupos344.cs"
    $designer = Join-Path $root "Views\Grupos344GruposVisaoContabil.Designer.cs"
    $view = Join-Path $root "Views\Grupos344GruposVisaoContabil.cs"
    Assert-FileMatch `
        -Name "CG00344 FORM 2 selects the complete groups form" `
        -Path $controller `
        -Pattern 'default:[\s\S]{0,300}?new Views\.GruposView\(this\);[\s\S]{0,120}?SetMainDisplayIndex\(2\);'
    Assert-FileMatch `
        -Name "CG00344 FORM 3 selects the accounting view" `
        -Path $controller `
        -Pattern 'case\s+3:[\s\S]{0,300}?new Views\.Grupos344GruposVisaoContabil\(this\);[\s\S]{0,120}?SetMainDisplayIndex\(3\);'
    Assert-FileMatch `
        -Name "CG00344 creates the declared MasterUI host" `
        -Path $designer `
        -Pattern 'Cigam\.Metro\.UI\.MasterUI\s+dnVTela;'
    Assert-FileMatch `
        -Name "CG00344 does not replace MasterUI with a Panel" `
        -Path $designer `
        -Pattern 'System\.Windows\.Forms\.Panel\s+dnVTela;' `
        -Not
    Assert-FileMatch `
        -Name "CG00344 assigns the MasterUI host to v_tela" `
        -Path $view `
        -Pattern '_controller\.v_tela\s*=\s*dnVTela;'
    Assert-FileMatch `
        -Name "CG00344 DNSet tolerates the absent MasterUI in FORM 2" `
        -Path $controller `
        -Pattern 'ExternalTypeCompat\.SetIfNotNull\(v_tela,\s*\(\)\s*=>\s*\(object\)\(v_tela\.metroPanelBarraLateral\.Visible\s*=\s*false\)\)'
    Assert-FileMatch `
        -Name "CG00344 emits the centralized null-safe DNSet helper" `
        -Path (Join-Path $root "ExternalTypeCompat.cs") `
        -Pattern 'internal static object SetIfNotNull\(object receiver,\s*Func<object> setter\)'
    Assert-FileMatch `
        -Name "CG00344 direct PesquisaFacil calls tolerate the resource absent in FORM 2" `
        -Path $controller `
        -Pattern 'ExternalTypeCompat\.InvokeIfNotNull\(v_pesquisaFacil,\s*\(\)\s*=>\s*v_pesquisaFacil\.AddDisplayMember\("Cd_grupo"\)\)'
    Assert-FileMatch `
        -Name "CG00344 emits the centralized null-safe invocation helper" `
        -Path (Join-Path $root "ExternalTypeCompat.cs") `
        -Pattern 'internal static void InvokeIfNotNull\(object receiver,\s*Action action\)'
    Assert-FileMatch `
        -Name "CG00344 includes its group-search SelectProgram dependency" `
        -Path (Join-Path $root "CG00376_PesquisaDeGrupos376.cs") `
        -Pattern 'class\s+CG00376_PesquisaDeGrupos376'
}

if ($selected -contains "CG02075") {
    $root = Get-ProjectRoot "CG02075"
    $controller = Join-Path $root "CG02075_Empresas_2075.cs"
    Assert-FileMatch `
        -Name "CG02075 FORM 3 selects the new complete company form" `
        -Path $controller `
        -Pattern 'default:[\s\S]{0,300}?new Views\.Empresas_2075Empresa\(this\);[\s\S]{0,120}?SetMainDisplayIndex\(3\);'
    Assert-FileMatch `
        -Name "CG02075 FORM 4 selects the simplified company form" `
        -Path $controller `
        -Pattern 'case\s+4:[\s\S]{0,300}?new Views\.Empresas_2075CadastroSimplificadoEmpresaPessoa\(this\);[\s\S]{0,120}?SetMainDisplayIndex\(4\);'
    Assert-FileMatch `
        -Name "CG02075 FORM 5 selects the legacy company form" `
        -Path $controller `
        -Pattern 'case\s+5:[\s\S]{0,300}?new Views\.Empresas_2075EmpresaInterfaceAntiga\(this\);[\s\S]{0,120}?SetMainDisplayIndex\(5\);'
}

if ($selected -contains "CG00374") {
    $root = Get-ProjectRoot "CG00374"
    Assert-FileMatch `
        -Name "CG00374 Estoque caption" `
        -Path (Join-Path $root "Views\ConsultaMateriaisConsultaDeMateriais_1.Designer.cs") `
        -Pattern 'btnBPesquisaEstoque\.Text\s*=\s*"&Estoque"'
    Assert-FileMatch `
        -Name "CG00374 Cancelar caption" `
        -Path (Join-Path $root "Views\ConsultaMateriaisConsultaMateriais.Designer.cs") `
        -Pattern 'btnBCancelar\.Text\s*=\s*"Cancela&r"'
    Assert-FileMatch `
        -Name "CG00374 grid text is not anchored" `
        -Path (Join-Path $root "Views\ConsultaMateriaisConsultaDeMateriais_1.Designer.cs") `
        -Pattern 'txtDescricao\.Anchor\s*=' `
        -Not
    Assert-FileMatch `
        -Name "CG00374 checkbox expressions are invoked" `
        -Path (Join-Path $root "Views\ConsultaMateriaisConsultaDeMateriais_1.cs") `
        -Pattern 'CheckBoxData\(\(\)\s*=>\s*u\.CastToBool\(_controller\.Exp_\d+\(\)\)\)'
    Assert-FileMatch `
        -Name "CG00374 checkbox expressions are not method groups" `
        -Path (Join-Path $root "Views\ConsultaMateriaisConsultaDeMateriais_1.cs") `
        -Pattern 'CheckBoxData\(\(\)\s*=>\s*u\.CastToBool\(_controller\.Exp_\d+\)\)' `
        -Not
}

if ($selected -contains "CG02103") {
    $root = Get-ProjectRoot "CG02103"
    Assert-FileMatch `
        -Name "CG02103 company DataView" `
        -Path (Join-Path $root "CG02103_EmpresasPesquisa2103.cs") `
        -Pattern 'From\s*=\s*EMPRESA\s*;'
    Assert-FileMatch `
        -Name "CG02103 division selector included" `
        -Path (Join-Path $root "CG02114_ConsultaDivisaoEmpresa2114.cs") `
        -Pattern 'class\s+CG02114_ConsultaDivisaoEmpresa2114'
    Assert-FileMatch `
        -Name "CG02103 division call resolved" `
        -Path (Join-Path $root "CG02103_EmpresasPesquisa2103.cs") `
        -Pattern 'CG02114_ConsultaDivisaoEmpresa2114'
    Assert-FileMatch `
        -Name "CG02103 division call is not external fallback" `
        -Path (Join-Path $root "CG02103_EmpresasPesquisa2103.cs") `
        -Pattern 'ExternalProgramCompat\.External_Obj2114' `
        -Not
}

if ($selected -contains "CG02109") {
    $root = Get-ProjectRoot "CG02109"
    Assert-FileMatch `
        -Name "CG02109 business-unit DataView" `
        -Path (Join-Path $root "CG02109_PesquisaDeUnDeNegCio.cs") `
        -Pattern 'From\s*=\s*UNIDADE_NEGOCIO\s*;'
    $designer = Join-Path $root "Views\PesquisaDeUnDeNegCioPesquisaDeUnidadeDeNegCio.Designer.cs"
    Assert-FileMatch `
        -Name "CG02109 complete UN code cell width" `
        -Path $designer `
        -Pattern 'txtUnidadeDeNegocio\.Size\s*=\s*new Size\((2[9-9]|[3-9][0-9]),'
    Assert-FileMatch `
        -Name "CG02109 complete company code cell width" `
        -Path $designer `
        -Pattern 'txtEmpresa\.Size\s*=\s*new Size\(([5-9][0-9]|[1-9][0-9]{2,}),'
}

if ($selected -contains "CG06301") {
    $root = Get-ProjectRoot "CG06301"
    $engineeringFile = Join-Path $root "CG06301_CadastroDeEngenharia.cs"
    Assert-FileMatch `
        -Name "CG06301 engineering DataView" `
        -Path $engineeringFile `
        -Pattern 'Where\.Add\(ENGENHARIA\.CodigoPai\.IsEqualTo\(p_codigoPai\)\)'
    Assert-FileMatch `
        -Name "CG06301 task-wide BeforeControlClick preserves subform navigation" `
        -Path $engineeringFile `
        -Pattern 'Handlers\.Add\(Command\.BeforeControlClick, HandlerScope\.CurrentTaskOnly\);[\s\S]{0,1600}?e\.Handled\s*=\s*false;'
    Assert-FileMatch `
        -Name "CG06301 scoped primary argument" `
        -Path (Join-Path $root "ScopedTaskCompat.cs") `
        -Pattern 'new CG06301_CadastroDeEngenharia\(\)\.Run\(ENV\.UserMethods\.Instance\.CastToText\(primaryArgument\)\)'

    $runtimeSource = "E:\Dlls\RuntimeCore\XPARuntimeCore.Box.dll"
    $runtimeMaterialized = Join-Path (Split-Path -Parent $root) "ExternalRefs\Runtime\XPARuntimeCore.Box.dll"
    $runtimeHashesMatch =
        (Test-Path -LiteralPath $runtimeSource -PathType Leaf) -and
        (Test-Path -LiteralPath $runtimeMaterialized -PathType Leaf) -and
        ((Get-FileHash -LiteralPath $runtimeSource).Hash -eq
            (Get-FileHash -LiteralPath $runtimeMaterialized).Hash)
    Add-Check `
        -Name "CG06301 authoritative runtime materialized" `
        -Passed $runtimeHashesMatch `
        -Evidence "$runtimeSource -> $runtimeMaterialized"

    if (-not $SkipRuntimeSmoke) {
        Assert-CG06301RuntimeSmoke
    }
}

if ($selected -contains "CG06388") {
    $root = Get-ProjectRoot "CG06388"
    $explosionFile = Join-Path $root "CG06388_ExplodeEngenha_GeraOP.cs"
    Assert-FileMatch `
        -Name "CG06388 explosion task generated" `
        -Path $explosionFile `
        -Pattern 'class\s+CG06388_ExplodeEngenha_GeraOP'
    Assert-FileMatch `
        -Name "CG06388 relation source dependency selected" `
        -Path $explosionFile `
        -Pattern 'Columns\.Add\(ENGENHARIA\.UnidadeNegocio\);'
    Assert-FileMatch `
        -Name "CG06388 root batch respects OpenTaskWindow=N" `
        -Path $explosionFile `
        -Pattern 'View\s*=\s*\(\)\s*=>\s*new Views\.PesquisaEngenhariaView\(this\);' `
        -Not
    Assert-FileMatch `
        -Name "CG06388 dynamic OpenTaskWindow expression emitted" `
        -Path $explosionFile `
        -Pattern 'if\s*\(\(_parent\.P_Acao\s*==\s*"T"\)\s*\|\|[\s\S]{0,180}?_parent\.P_NaoAbrirTela'
    Assert-FileMatch `
        -Name "CG06388 dynamic CloseTaskWindow expression emitted" `
        -Path $explosionFile `
        -Pattern 'BindKeepViewVisibleAfterExit\(\(\)\s*=>\s*!\(\(_parent\.P_Acao\s*==\s*"T"\)'
    Assert-FileMatch `
        -Name "CG06388 Main Display preserves global form reference 5" `
        -Path $explosionFile `
        -Pattern 'case\s+5:[\s\S]{0,400}?SetMainDisplayIndex\(5\);'
}

if ($selected -contains "CG06353Flow") {
    $root = Get-ProjectRoot "CG06353Flow"
    Assert-FileMatch `
        -Name "CG06353 typed SQL date fallback" `
        -Path (Join-Path $root "CG06353_OrdemDeProduO.cs") `
        -Pattern 'NormalizeDynamicSqlDateNullFallback'
    Assert-FileMatch `
        -Name "CG06353 SQL Server date fallback uses a typed zero-date literal" `
        -Path (Join-Path $root "Shared\XpaSqlStorage.cs") `
        -Pattern 'CONVERT\(date,''19000101'',112\)'
    Assert-FileMatch `
        -Name "CG06353 invalid int-to-date conversion is absent" `
        -Path (Join-Path $root "Shared\XpaSqlStorage.cs") `
        -Pattern 'CONVERT\(date,0\)' `
        -Not
    Assert-FileMatch `
        -Name "CG06353 SQL Server time fallback uses typed midnight" `
        -Path (Join-Path $root "Shared\XpaSqlStorage.cs") `
        -Pattern 'CONVERT\(time,''00:00:00''\)'
    Assert-FileMatch `
        -Name "CG06353 time comparison uses a database-specific suffix argument" `
        -Path (Join-Path $root "CG06353_OrdemDeProduO.cs") `
        -Pattern ':7mpr\.hora_final:(?<suffix>\d+)\s*=\s*:7mop\.hora_final_mvto_op:\k<suffix>'
    Assert-FileMatch `
        -Name "CG06353 raw numeric SQL time fallback is absent" `
        -Path (Join-Path $root "CG06353_OrdemDeProduO.cs") `
        -Pattern 'hora_final(?:_mvto_op)?,0\)' `
        -Not
    Assert-FileMatch `
        -Name "CG06353 time suffix is emitted through central SQL compatibility" `
        -Path (Join-Path $root "CG06353_OrdemDeProduO.cs") `
        -Pattern 'ResolveDynamicSqlTimeNullFallbackSuffix'
    Assert-FileMatch `
        -Name "CG06353 raw numeric SQL date fallback removed" `
        -Path (Join-Path $root "CG06353_OrdemDeProduO.cs") `
        -Pattern 'sqlEntity\.AddParameter\(\(\)\s*=>\s*u\.If\([^\r\n]+",0\)"' `
        -Not
    Assert-FileMatch `
        -Name "CG06353 engineering DataView filter" `
        -Path (Join-Path $root "CG06301_CadastroDeEngenharia.cs") `
        -Pattern 'Where\.Add\(ENGENHARIA\.CodigoPai\.IsEqualTo\(p_codigoPai\)\)'
    Assert-FileMatch `
        -Name "CG06353 engineering subform navigation preserved" `
        -Path (Join-Path $root "CG06301_CadastroDeEngenharia.cs") `
        -Pattern 'Handlers\.Add\(Command\.BeforeControlClick, HandlerScope\.CurrentTaskOnly\);[\s\S]{0,1600}?e\.Handled\s*=\s*false;'
    Assert-FileMatch `
        -Name "CG06353 explosion included" `
        -Path (Join-Path $root "CG06388_ExplodeEngenha_GeraOP.cs") `
        -Pattern 'class\s+CG06388_ExplodeEngenha_GeraOP'
    Assert-FileMatch `
        -Name "CG06353 order number service included" `
        -Path (Join-Path $root "CG06385_Atual_Ordem_Demanda_Geral.cs") `
        -Pattern 'class\s+CG06385_Atual_Ordem_Demanda_Geral'
    Assert-FileMatch `
        -Name "CG06353 order number service call resolved" `
        -Path (Join-Path $root "CG06353_OrdemDeProduO.cs") `
        -Pattern 'ExternalProgramCompat\.External_Obj6385' `
        -Not
    Assert-FileMatch `
        -Name "CG06353 correlates cleanup included" `
        -Path (Join-Path $root "CG06435_ExcluiCorrelOP.cs") `
        -Pattern 'class\s+CG06435_ExcluiCorrelOP'
    Assert-FileMatch `
        -Name "CG06353 correlates cleanup call resolved" `
        -Path (Join-Path $root "CG06353_OrdemDeProduO.cs") `
        -Pattern 'ExternalProgramCompat\.External_Obj6435' `
        -Not
    Assert-FileMatch `
        -Name "CG06353 explosion relation source dependency selected" `
        -Path (Join-Path $root "CG06388_ExplodeEngenha_GeraOP.cs") `
        -Pattern 'Columns\.Add\(ENGENHARIA\.UnidadeNegocio\);'
    Assert-FileMatch `
        -Name "CG06353 engineering validator included" `
        -Path (Join-Path $root "CG06391_ValidaRegistroEngenharia.cs") `
        -Pattern 'class\s+CG06391_ValidaRegistroEngenharia'
    Assert-FileMatch `
        -Name "CG06353 engineering validator call resolved" `
        -Path (Join-Path $root "CG06388_ExplodeEngenha_GeraOP.cs") `
        -Pattern 'ExternalProgramCompat\.External_Obj6391' `
        -Not
    Assert-FileMatch `
        -Name "CG06353 demand generator wrapper included" `
        -Path (Join-Path $root "NAOUSAR_CHAMAR_PC00019.cs") `
        -Pattern 'CGPCCore\.PC00019_CG06365CriaDemandOPsLotePVPS'
    Assert-FileMatch `
        -Name "CG06353 demand generator call resolved" `
        -Path (Join-Path $root "CG06388_ExplodeEngenha_GeraOP.cs") `
        -Pattern 'ExternalProgramCompat\.External_Obj6365' `
        -Not
    Assert-FileMatch `
        -Name "CG06353 talon lookup included" `
        -Path (Join-Path $root "CG06446_PesquisaTalEs.cs") `
        -Pattern 'class\s+CG06446_PesquisaTalEs'
    Assert-FileMatch `
        -Name "CG06353 talon lookup call resolved" `
        -Path (Join-Path $root "CG06353_OrdemDeProduO.cs") `
        -Pattern 'ExternalProgramCompat\.External_Obj6446' `
        -Not
    Assert-FileMatch `
        -Name "CG06353 print flow included" `
        -Path (Join-Path $root "CG06464_EmissODaOP6464.cs") `
        -Pattern 'class\s+CG06464_EmissODaOP6464'
    Assert-FileMatch `
        -Name "CG06353 print filter included" `
        -Path (Join-Path $root "CG06404_FiltrarOrdens.cs") `
        -Pattern 'class\s+CG06404_FiltrarOrdens'
    Assert-FileMatch `
        -Name "CG06464 print filter call resolved" `
        -Path (Join-Path $root "CG06464_EmissODaOP6464.cs") `
        -Pattern 'ExternalProgramCompat\.External_Obj6404' `
        -Not
    $printFilterView = Join-Path $root "Views\EmissODaOP6464FiltrarGeralByExp.cs"
    Assert-FileMatch `
        -Name "CG06464 print button retains its XPA column identity" `
        -Path $printFilterView `
        -Pattern 'btnBConfirmar\.Data\s*=\s*\(XPARuntimeCore\.Box\.UI\.Advanced\.ButtonData\)_controller\.B_Confirmar;'
    Assert-FileMatch `
        -Name "CG06464 cancel button retains its XPA column identity" `
        -Path $printFilterView `
        -Pattern 'btnBCancelar\.Data\s*=\s*\(XPARuntimeCore\.Box\.UI\.Advanced\.ButtonData\)_controller\.B_Cancelar;'
    Assert-FileMatch `
        -Name "CG06464 print filter buttons do not lose column identity to caption lambdas" `
        -Path $printFilterView `
        -Pattern 'ButtonData\(\(\)\s*=>[\s\S]{0,200}?_controller\.B_(Voltar|Avancar|Confirmar|AlterarModelo|Cancelar)' `
        -Not
    Assert-FileMatch `
        -Name "CG06353 print call resolved" `
        -Path (Join-Path $root "CG06353_OrdemDeProduO.cs") `
        -Pattern 'ExternalProgramCompat\.External_Obj6464' `
        -Not
    $availabilityView = Join-Path $root "Views\DisponibilidadeMaterialDisponibilidadeByExp.Designer.cs"
    Assert-FileMatch `
        -Name "CG06393 availability view is included in the integrated slice" `
        -Path $availabilityView `
        -Pattern 'partial class DisponibilidadeMaterialDisponibilidadeByExp'
    Assert-FileMatch `
        -Name "CG06393 availability view does not emit invalid negative TabIndex" `
        -Path $availabilityView `
        -Pattern '\.TabIndex\s*=\s*-\d+' `
        -Not
    $kernelVisualizer = Join-Path `
        (Split-Path -Parent $SlicesRoot) `
        "projects\04-CGKernel\CGKernel\Views\_2237ModelosRelatoriosVisualizaRelatRio.cs"
    Assert-FileMatch `
        -Name "CGKernel report viewer button retains its XPA column identity" `
        -Path $kernelVisualizer `
        -Pattern 'btnBVisualizarRelatorio\.Data\s*=\s*\(XPARuntimeCore\.Box\.UI\.Advanced\.ButtonData\)_controller\.B_VisualizarRelatorio;'
    Assert-FileMatch `
        -Name "CGKernel report viewer does not replace the button column with a caption lambda" `
        -Path $kernelVisualizer `
        -Pattern 'btnBVisualizarRelatorio\.Data\s*=\s*new\s+XPARuntimeCore\.Box\.UI\.Advanced\.ButtonData' `
        -Not
    $kernelReportController = Join-Path `
        (Split-Path -Parent $SlicesRoot) `
        "projects\04-CGKernel\CGKernel\CK02237_2237ModelosRelatorios.cs"
    Assert-FileMatch `
        -Name "CGKernel report viewer keeps automatic visualization flow" `
        -Path $kernelReportController `
        -Pattern 'protected override void OnStart\(\)[\s\S]{0,300}?Raise\(Command\.GoToNextControl\);[\s\S]{0,120}?Raise\(Command\.Expand\);'
    $kbPutParser = "D:\Projetos_CSharp\XPARuntimeCore\ENV\Utilities\KBPutParser.cs"
    Assert-FileMatch `
        -Name "Runtime maps legacy modify command" `
        -Path $kbPutParser `
        -Pattern 'AddCommandAlias\("Modify Records", XPARuntimeCore\.Box\.Command\.SwitchToUpdateActivity\)'
    Assert-FileMatch `
        -Name "Runtime maps legacy screen refresh command" `
        -Path $kbPutParser `
        -Pattern 'AddCommandAlias\("Screen Refresh", XPARuntimeCore\.Box\.Command\.RefreshDisplayedData\)'
    Assert-FileMatch `
        -Name "Runtime maps legacy user actions" `
        -Path $kbPutParser `
        -Pattern 'AddCommandAlias\("User Action " \+ index, command\)'
    $runtimeTask = "D:\Projetos_CSharp\XPARuntimeCore\XPARuntimeCore.Box\Task.cs"
    Assert-FileMatch `
        -Name "Runtime recomputes activity-bound controls after mode switch" `
        -Path $runtimeTask `
        -Pattern 'SwitchToActivity\(ActivityStrategy newActivityStrategy, bool causedByCommand\)[\s\S]{0,500}?ActivityChanged\(\);[\s\S]{0,200}?_recomputeManager\.ComputeNonDataItems\(forCurrentRow: true\);'
    Assert-FileMatch `
        -Name "CG06353 demand action keeps only residual conditions" `
        -Path (Join-Path $root "CG06388_ExplodeEngenha_GeraOP.cs") `
        -Pattern 'if \(\(\(_parent\.P_Acao == "G"\)[^\r\n]*\(_parent\.P_OrdemGeral > 0\)\)\)\s*\{\s*Cached<global::CGGeral\.CG06388_ExplodeEngenha_GeraOP\.ExplEng_GeraOP_SQL\.CriaDemandasOP>\(\)\.Run\(\);'
    Assert-FileMatch `
        -Name "CG06353 numeric launcher argument parsed" `
        -Path (Join-Path $root "ScopedTaskCompat.cs") `
        -Pattern 'new CG06353_OrdemDeProduO\(\)\.Run\(XPARuntimeCore\.Box\.Number\.Parse\(primaryArgument\)\)'
    Assert-FileMatch `
        -Name "CG06353 talon creation included" `
        -Path (Join-Path $root "CG06401_CriaTalao.cs") `
        -Pattern 'class\s+CG06401_CriaTalao'
    Assert-FileMatch `
        -Name "CG06353 company division dependency included" `
        -Path (Join-Path $root "CG02103_EmpresasPesquisa2103.cs") `
        -Pattern 'CG02114_ConsultaDivisaoEmpresa2114'
    Assert-FileMatch `
        -Name "CG06353 company division call is not external fallback" `
        -Path (Join-Path $root "CG02103_EmpresasPesquisa2103.cs") `
        -Pattern 'ExternalProgramCompat\.External_Obj2114' `
        -Not
    $kernelMessage = Join-Path `
        (Split-Path -Parent $SlicesRoot) `
        "projects\04-CGKernel\CGKernel\CK02149_2149MensagemOk.cs"
    Assert-FileMatch `
        -Name "CGKernel legacy native message box remains disabled" `
        -Path $kernelMessage `
        -Pattern 'if\s*\(\(false\)\)\s*\{[\s\S]{0,6000}?CallDLL\("USER32\.MessageBoxA"'
    Assert-FileMatch `
        -Name "CGKernel message uses only MensagemCIGAM dialog" `
        -Path $kernelMessage `
        -Pattern 'Cached<CGFunctions\.MensagemCIGAM_MensagemCigam>\(\)\.Run'
}

$reportDirectory = Join-Path $SlicesRoot "reports"
New-Item -ItemType Directory -Force -Path $reportDirectory | Out-Null
$reportPath = Join-Path $reportDirectory "slice-regression.json"
$checks | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $reportPath -Encoding UTF8

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    throw "$($failures.Count) teste(s) de recorte falharam. Relatorio: $reportPath"
}

Write-Host "$($checks.Count) teste(s) de recorte passaram. Relatorio: $reportPath"
