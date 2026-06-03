# XpaConverterMvp

Conversor MVP de XML Magic XPA para C#.

Documentos de apoio:
- [SPEC_DRIVEN_DEVELOPMENT.md](/c:/Users/mfsnh/Downloads/Application/tools/XpaConverterMvp/SPEC_DRIVEN_DEVELOPMENT.md)

Fontes de evidencia usadas pelo conversor, em ordem:
1. XML do projeto XPA
2. conversao original oficial, quando existir
3. help do Magic xpa
4. fontes reais do runtime Firefly / ENV

Escopo atual:
- Parse de `ModelsRepository` (campos/tipos).
- Parse de `DataSourceRepository` (DataObjects, colunas, indices).
- Parse de `ProgramsRepository` (tasks).
- Parse inicial de `TaskLogic/LogicLines` para:
  - `DATAVIEW_SRC`
  - `Select`
  - `LNK`/`END_LINK`
  - `CallTask`, `Update`, `STP` em unidades de handler (`Level=H`/eventos)
  - `CallTask` `OperationType=T` para fluxo/tab (`Flow.Add(..., FlowMode.Tab, ...)`)
  - logica de linha (`Level=R/Type=P`, `Modifier=B`) para `OnEnterRow` preservando a ordem das acoes
  - `EVNT` e `Expressions` por task
  - hierarquia de task aninhada (`Task` dentro de `Task`) para gerar classes internas
- Geracao de codigo para:
  - `Types/*.cs`
  - `Models/*.cs`
  - `*.generated.cs` de programas com:
    - `InitializeDataView()`
    - `InitializeHandlers()` com `CustomCommand` e blocos `Handlers.Add(...).Invokes`
    - `Run(...)` com parametros de task (`BindParameter`)
    - `MarkParameterColumns(...)`
    - `OnLoad()` inicial (locking/transaction/activity/select/export/view)
  - `ApplicationEntitiesMvp.generated.cs`
  - `ApplicationProgramsMvp.generated.cs`

## Como executar

```powershell
$env:DOTNET_CLI_HOME='C:\Users\mfsnh\Downloads\Application'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
dotnet run --project tools/XpaConverterMvp/XpaConverterMvp.csproj -- `
  "C:\Users\mfsnh\Downloads\Northwind_XPA\Files For Migration\Northwind.xml" `
  "C:\Users\mfsnh\Downloads\Application\tools\XpaConverterMvp\out\NorthwindGenerated" `
  "Northwind.MvpGen"
```

## Limites do MVP

1. `TaskLogic` ainda e parcial:
   - gera origem principal, `Columns.Add(...)`, `Relations.Add(...)` e handlers base;
   - relacoes `LNK` usam heuristica de chave/coluna (pode divergir em casos complexos).
2. `TaskForms`/designer ainda nao sao gerados.
3. Mapeamentos de compatibilidade XPA avancados ainda nao foram implementados (ex.: argumentos de `CallTask`, expressoes completas em C#, regras de `STP`).
   - `CallTask.OperationType` (`P`/`T`/`O`) agora gera semantica no handler e `T` em fluxo/tab quando identificado.
   - `STP` agora gera `Message.ShowError/ShowWarning` com `Mode`, `TXT`/`Exp`, `Condition Exp`, `TitleTxt`, `Buttons`, `Image`, `DefaultButton`.
   - ainda faltam partes de paridade (ex.: `FlowMode.ExpandBefore`, `Groups`, streams/layout de impressao, refinamento de assinaturas/nomes para ficar 1:1 com codigo original).
4. `BindAllowInsert` ainda usa placeholder de expressao (`true /* Exp N: ... */`), sem traducao completa para lambda C# funcional.

## Proximo passo recomendado

Implementar tradutor de `TaskLogic/LogicLines` para gerar:
- `InitializeDataView()` (`From`, `Relations`, `Where`, `OrderBy`, `Columns.Add`).
- `InitializeHandlers()` para eventos/atalhos/comandos.
- Overloads de `Run(...)` com parametros de task.
