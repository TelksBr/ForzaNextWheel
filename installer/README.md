# Distribuição e Instaladores

## Compilação Local

Execute o script de release:
```powershell
powershell -ExecutionPolicy Bypass -File scripts/build-release.ps1
```
Ou para compilar apenas o instalador MSI:
```powershell
powershell -ExecutionPolicy Bypass -File scripts/build-msi.ps1
```

O resultado é gerado em `artifacts/release/<versão>`:
- `ForzaNextWheel-<versão>-Setup.msi`: Instalador padrão do Windows (WiX v5 Toolset, arquitetura x64, per-machine em Program Files).
- `ForzaNextWheel-<versão>-Setup.exe`: Instalador Inno Setup (se compilador presente).
- `ForzaNextWheel-<versão>-Portable.zip`: Pacote portátil autossuficiente (.NET 8 runtime embutido, sem necessidade de instalação).
- `release.json`: Manifesto de versão e checksum SHA-256 do instalador para o atualizador do app.
- `checksums.txt`: Lista de hashes SHA-256 de todos os artefatos da release.

O driver vJoy assinado (2.2.2.0) fica embutido na aplicação; sua preparação administrativa ocorre na inicialização do aplicativo caso não esteja presente no sistema. Atualizar ou desinstalar preserva as configurações do usuário em `%LOCALAPPDATA%/ForzaNextWheel` e a instalação do vJoy.

## WiX Toolset v5

O instalador MSI é gerado com **WiX Toolset v5.0.2** configurado via arquivo de manifesto local `.config/dotnet-tools.json`. Não é necessária nenhuma instalação global; o comando `dotnet tool restore` restaura a ferramenta automaticamente em qualquer ambiente (local ou CI/CD GitHub Actions).

Fontes e documentação do WiX: https://wixtoolset.org/

## Publicação Automática no GitHub Releases

O fluxo de automação `.github/workflows/release.yml` é disparado automaticamente ao criar e enviar uma tag de versão:
```bash
git tag v2.1.2
git push origin v2.1.2
```
Ele executa:
1. Validação e testes de regressão (`dotnet test -c Release`).
2. Download e verificação da dependência vJoy.
3. Compilação do aplicativo self-contained x64 (`ForzaNext.UI` e `ForzaNext.Cli`).
4. Geração do instalador MSI (`.msi`) com WiX v5 e pacote portátil (`.zip`).
5. Geração dos checksums SHA-256 e `release.json`.
6. Publicação no GitHub Releases com notas geradas automaticamente e todos os binários anexados.
