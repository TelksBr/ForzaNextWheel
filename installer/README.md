# Distribuição

Execute `pwsh -File scripts/build-release.ps1` após `scripts/check.ps1`.
O resultado em `artifacts/release/2.1.2` inclui instalador por usuário, pacote portátil
autossuficiente (.NET incluído) e `release.json` com SHA-256 e tamanho do instalador.
O driver vJoy assinado fica embutido; sua preparação administrativa ocorre na abertura
do app. Atualizar ou desinstalar preserva `%LOCALAPPDATA%/ForzaNextWheel` e o vJoy.
O instalador exige o app fechado e não termina processos nem sessões do volante.

O compilador é Inno Setup 6.7.3 (Pyrsys B.V.), extraído do pacote NuGet
`Tools.InnoSetup` 6.7.3, sem instalação no Windows. Fontes:
https://jrsoftware.org/isdl.php e https://www.nuget.org/packages/Tools.InnoSetup/6.7.3
SHA-256 do nupkg: `F780898E402FF80612CC8D9FCB8C6E02932BD1CB4C900FFDAA31F9341CFB49F4`.
Confira a licença da ferramenta antes de distribuição comercial.

Para reproduzir, baixe o nupkg da API pública NuGet, confira o hash, extraia em
`tools/InnoSetup` e confirme a assinatura Authenticode válida de `tools/ISCC.exe`.
Também é possível fornecer o caminho de uma instalação própria com `-Compiler`.

Em Diagnóstico, selecione o `release.json` de uma versão superior, junto do instalador.
O app copia o instalador para uma área temporária, confere produto, versão, nome,
tamanho e SHA-256, pede confirmação e encerra a sessão e o dispositivo antes de abri-lo.
O checksum detecta corrupção; não autentica o publicador. Use pacotes de confiança.
Não há servidor de atualização configurado nem assinatura de publicação própria.
