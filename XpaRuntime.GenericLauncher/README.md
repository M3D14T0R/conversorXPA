# XPA Runtime Generic Launcher

Launcher genérico para projetos emitidos pelo conversor atual (`XPARuntimeCore.Box`).

- EXE convertido: iniciado como processo, preservando o `.config` e o diretório do projeto.
- DLL convertida: carregada por reflexão e executada por `EntryPoint` ou `Program.Main`.
- Configuração por INI, com diretórios adicionais de probing.
- No modo `Process`, DLLs ausentes são preparadas no diretório da aplicação
  a partir dos `ProbePaths`. Arquivos próprios da aplicação não são
  sobrescritos; somente arquivos previamente preparados pelo launcher podem ser
  atualizados nas execuções seguintes.
- `RefreshProbeFiles=Y` torna os `ProbePaths` autoritativos e substitui cópias
  antigas no diretório da aplicação. Sem essa opção, a proteção acima continua
  sendo o comportamento padrão.
- EXE gerado pelo conversor: `StartProgram` abre um programa público depois que o MDI estiver pronto.
- `MagicIni` é encaminhado como `/ini=...`, permitindo carregar bancos e nomes lógicos do ambiente XPA.
- Entradas de `[Parameters]` são aplicadas como parâmetros XPA antes da abertura do MDI.

- `ControllerTrace` accepts a semicolon-separated controller list and records
  task type, lifecycle and group events, handlers, DataView, effective relation
  filters and `RowFound`, row snapshots, explicit final column changes, processed
  row counts and duration. Password/token-like column names are redacted.
- `RuntimeProfilerFile` enables the runtime profiler for call hierarchy, readers,
  relations with no rows and executed SQL. `RuntimeProfilerTrace=Y` also writes
  its detailed timeline.

Diagnostic example:

```ini
[Diagnostics]
ControllerTrace=CG06353;CG06388;CG06401
ControllerTraceFile=diagnostics\controllers.log
RuntimeProfilerFile=diagnostics\runtime.prof
RuntimeProfilerTrace=Y
```

Uso:

```text
XpaRuntime.GenericLauncher.exe --ini XpaRuntime.GenericLauncher.CGGeral.ini
XpaRuntime.GenericLauncher.exe --ini XpaRuntime.GenericLauncher.CG00347.ini
XpaRuntime.GenericLauncher.exe --assembly C:\Convertido\Aplicacao.exe
XpaRuntime.GenericLauncher.exe --ini app.ini --validate
XpaRuntime.GenericLauncher.exe --ini app.ini --program CG00347 --magic-ini C:\CIGAM\magic.ini
```

Argumentos depois de `--` são encaminhados à aplicação convertida.
