# XPA Runtime Generic Launcher

Launcher genérico para projetos emitidos pelo conversor atual (`XPARuntimeCore.Box`).

- EXE convertido: iniciado como processo, preservando o `.config` e o diretório do projeto.
- DLL convertida: carregada por reflexão e executada por `EntryPoint` ou `Program.Main`.
- Configuração por INI, com diretórios adicionais de probing.

Uso:

```text
XpaRuntime.GenericLauncher.exe --ini XpaRuntime.GenericLauncher.CGGeral.ini
XpaRuntime.GenericLauncher.exe --assembly C:\Convertido\Aplicacao.exe
XpaRuntime.GenericLauncher.exe --ini app.ini --validate
```

Argumentos depois de `--` são encaminhados à aplicação convertida.
