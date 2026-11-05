# ForzaNext Wheel

Aplicativo Windows x64 em .NET 8/WPF para agregar volante, pedais e câmbio no dispositivo **vJoy 1**, com encaminhamento de Force Feedback por DirectInput.

## Compilar e verificar

Pré-requisitos: Windows 10/11 x64 e SDK .NET 8. O build restaura SharpDX.DirectInput 4.2.0 pelo NuGet; não depende das DLLs em `lib` ou de diretórios locais do EmuWheel.

```powershell
./scripts/fetch-vjoy.ps1
dotnet restore ForzaNextWheel.sln --locked-mode
dotnet build ForzaNextWheel.sln -c Release --no-restore
dotnet test ForzaNextWheel.sln -c Release --no-build --no-restore
dotnet run --project src/ForzaNext.Cli -c Release --no-build -- validate configuration.json
```

Ou execute `./scripts/check.ps1`. Os testes não abrem dispositivos nem enviam torque. O projeto CLI é um validador de perfis, não um feeder de terminal.

Para gerar a distribuição:

```powershell
dotnet publish src/ForzaNext.UI -c Release -r win-x64 --self-contained false -o artifacts/publish
```

A pasta de publicação contém o aplicativo, dependências gerenciadas e o perfil inicial. A máquina de destino precisa do .NET Desktop Runtime 8 x64. O aplicativo verifica o vJoy e oferece instalação e preparação integradas com autorização do Windows. A DLL nativa é carregada da instalação em `Program Files/vJoy/x64`; uma DLL colocada explicitamente ao lado do aplicativo é alternativa de distribuição e precisa corresponder ao driver. Não misture DLLs x86 e x64.

## Configuração e uso

A interface tem quatro áreas: **Visão geral**, **Controles**, **Force Feedback** e **Diagnóstico**. O botão de sessão fica sempre no cabeçalho. O editor agrupa os controles por função, permite desfazer a última alteração de mapeamento e indica ajustes ainda não salvos. Use **Ctrl+1–4** para navegar, **Ctrl+S** para salvar e **Esc** para cancelar uma detecção.

O encaminhamento FFB usa um atuador físico de volante: direção cartesiana zero e magnitude assinada, sem multiplicar o ganho pelo ângulo de um joystick de dois eixos. Se a abertura do FFB exceder o prazo, a sessão registra a etapa nativa exata e mantém as entradas funcionando. A tela de FFB e a visão geral mostram essa causa; uma expiração na aquisição do volante não é apresentada como falha de instalação do vJoy. Uma nova tentativa de torque exige parar e iniciar a sessão.

Se o log parar em **Adquirindo acesso DirectInput**, teste o volante diretamente numa porta USB da placa-mãe. Nesta investigação, dois hubs em cascata bloqueavam a aquisição; a conexão direta permitiu inicializar o FFB. Para verificar apenas inicialização/encerramento com ganho zero, use `./scripts/check-worker.ps1 -UiExecutable artifacts/publish/ForzaNext.UI.dll -ProfilePath <perfil.json> -VerifyFfb`. Essa verificação não envia comandos de teste do motor e não grava ajustes no perfil.

Para conferir o visual sem instalar/ativar o vJoy, ler dispositivos ou enviar força, execute `ForzaNext.UI.exe --preview`. A prévia identifica explicitamente os dados simulados e não grava o perfil. Depois do build Release, `./scripts/check-ui.ps1` verifica as interações e gera imagens das quatro telas em dois tamanhos em `artifacts/ui-preview`.

1. Abra o aplicativo e autorize o gerenciador na janela do Windows. A aplicação instala o driver quando necessário, ativa o vJoy 1 e confere/aplica automaticamente 128 botões, 8 eixos, um POV contínuo e os efeitos FFB. O botão de preparação serve para repetir a operação caso necessário. Pode ser preciso reiniciar o Windows após a primeira instalação e então reabrir a aplicação.
2. Abra a interface, pare outros feeders e mapeie os controles físicos. FFB é detectado pelas capacidades do dispositivo; o destino é o perfil com `FFBParameters`, o volante com direção mapeada ou o único dispositivo FFB disponível. Em caso de ambiguidade, a sessão pede uma seleção explícita no perfil em vez de adivinhar.
3. Salve e inicie o feeder. A interface diferencia entradas ativas de FFB indisponível e exibe taxa real de atualização e maior intervalo medido na última janela de um segundo. O alvo de 1 ms não é garantia de 1000 Hz no Windows.
4. Se perder uma leitura ou falhar uma atualização do vJoy, a sessão interrompe o FFB, envia entradas neutras e libera os dispositivos. Reconecte, atualize os dispositivos e inicie novamente. O torque não reinicia automaticamente.
5. Pare a sessão antes de detectar, editar, salvar ou recarregar perfis. A parada aguarda brevemente o processo proprietário; se o driver não retornar, o processo auxiliar é encerrado sem bloquear a janela.

A interface funciona sem elevação. Configuração e logs ficam em `%LOCALAPPDATA%/ForzaNextWheel`. Na primeira abertura, o perfil inicial ao lado do executável é copiado para essa pasta. Cada salvamento preserva a versão anterior em `configuration.json.bak`; a gravação usa arquivo temporário e troca atômica. Se o arquivo estiver corrompido, feche o aplicativo e restaure esse backup antes de reabrir.

Cada ação deve aparecer uma vez no conjunto de perfis. `Deadzone` aceita 0 a 0,99. O RB (botão virtual 7) é independente do freio de mão digital (26). `DPad.Index` escolhe o POV físico entre 0 e 3. A inversão de cada eixo é salva no dispositivo que contém seu mapeamento. O produto e o InstanceGuid são guardados; uma mudança de InstanceGuid só é reconciliada quando existe um produto correspondente sem ambiguidade.

## Force Feedback

O callback copia os pacotes enquanto a memória nativa é válida e os coloca em fila limitada. Toda criação, atualização, parada e liberação de efeitos ocorre na thread proprietária de um processo auxiliar oculto. A interface troca configuração e telemetria por um pipe restrito ao usuário atual. Cada EffectBlockIndex tem relatório, parâmetros e estado próprios.

São encaminhados força constante, rampa, seno, quadrada, triangular, dente de serra, mola, damper, inércia e fricção, quando suportados pelo volante. São tratados gain do dispositivo/efeito, direção projetada no atuador X, envelope, duração, iterações, start, solo, stop, pause, continue, reset e liberação de blocos. Dados de efeitos customizados/amostrados não são implementados. O indicador mostra o comando estimado de força constante, não torque físico medido.

Testes manuais têm duração finita, são exclusivos entre si e pertencem à sessão; parar cancela os comandos seguintes. A interface não envia torque automaticamente ao abrir.

## GameInput e scripts administrativos

Os scripts em `scripts` são opcionais e não são executados pelo aplicativo ou pelos testes. O modo nativo ODrive e o modo de agregação vJoy são configurações distintas. Os índices GameInput do firmware precisam ser conferidos; não são necessariamente a enumeração DirectInput.

`apply_direct_gameinput.ps1` exige elevação, aceita índices explícitos, rejeita eixos duplicados e exporta o registro antes de alterar. Sem `-HandbrakeAxis`, omite o freio de mão em vez de associá-lo à direção. Oferece `-WhatIf`.

`uninstall_vjoy_complete.ps1` chama somente o desinstalador oficial, verifica o código de saída e oferece `-WhatIf`. Não remove INF/dispositivos por números fixos, arquivos do Windows ou pastas do projeto. Se não encontrar o desinstalador, encerra sem fazer remoções.

O aplicativo não oculta dispositivos físicos nem modifica automaticamente o registro do GameInput. A configuração específica do jogo precisa evitar mapeamentos duplicados entre dispositivos físicos e virtuais.

## Limites de validação

Os testes automatizados verificam contratos binários, redução de pacotes, matemática de parâmetros, perfis, reconciliação de identidade e agregação sem hardware. A compatibilidade de um firmware ODrive/vJoy/Forza específico, a sensação do volante e a latência física exigem ensaio de integração. A aquisição com FFB tem limite de 8 segundos. Se não responder, o processo auxiliar é encerrado e há uma tentativa nova somente com entradas, sem aquisição exclusiva de FFB. A segunda tentativa também tem limite; uma sessão ativa sem progresso é encerrada após 5 segundos. Isso mantém a interface utilizável mesmo quando uma chamada nativa trava. Neste setup, a aquisição voltou a funcionar ao conectar o ODrive diretamente à placa-mãe. A cadeia anterior tinha dois hubs USB em cascata. Isso não comprova um defeito no firmware nem identifica uma atualização específica do Windows.

## Dependências e fontes

- [SharpDX 4.2.0](https://github.com/sharpdx/SharpDX): MIT; distribuído via NuGet, licença no pacote. O upstream está arquivado; a integração fica concentrada no Core para facilitar uma migração futura.
- [vJoy](https://github.com/shauleiz/vJoy): projeto e termos de licença no repositório/instalador oficial. O build pode embutir o instalador assinado Brunner 2.2.2.0; a licença original acompanha a publicação. O driver é instalado automaticamente quando ausente, após a autorização do Windows na abertura da aplicação. As DLLs nativas são carregadas da instalação correspondente.
- As pastas locais de referência, decompilação, ferramentas, dumps e binários antigos não fazem parte da distribuição de produção.


## Diagnóstico da inicialização

O status mostra a etapa em andamento (vJoy, aquisição física ou efeitos). Se o dispositivo vJoy não tiver eixos/botões suficientes, a mensagem informa a configuração exigida antes de tentar adquirir FFB. Para o perfil inicial do projeto, o botão virtual 27 é usado pelo freio de mão digital; configure 128 botões no dispositivo vJoy 1 para permitir todos os mapeamentos da interface.

`./scripts/check-worker.ps1` verifica o processo auxiliar e o IPC sem abrir hardware, usando um perfil vazio que deve ser recusado. Com `-ProfilePath <arquivo>`, faz um teste real somente de entradas, sem FFB: verifica inicialização, três amostras e parada. Essa verificação não altera o perfil informado.


## Preparação integrada e distribuição do vJoy

O botão de preparação usa o utilitário da instalação existente e altera apenas o dispositivo vJoy 1. Antes de alterar, exporta os parâmetros do driver para `%PROGRAMDATA%/ForzaNextWheel/Backups`. Uma sessão ocupada bloqueia a reconfiguração. Feche jogos e outros feeders antes de preparar, pois o Windows pode reenumerar os dispositivos vJoy. O resultado é conferido em um novo processo, consultando as capacidades reais; o código de saída do utilitário não basta para anunciar sucesso.

Para novas instalações, a distribuição fixada é [BrunnerInnovation/vJoy 2.2.2.0](https://github.com/BrunnerInnovation/vJoy/releases/tag/v2.2.2.0), assinada pelo fornecedor para Windows 10 e 11. O hash e a procedência estão em `dependencies/vjoy/README.md`. O build local inclui o instalador como recurso interno; `scripts/fetch-vjoy.ps1` prepara esse recurso para builds novos/CI. Se ele não estiver embutido, o botão baixa o mesmo arquivo fixado e só o executa após validar seu SHA-256. A integração não troca automaticamente um driver existente por outra versão.

A instalação/configuração e a desativação administrativas não são executadas pelos testes. O ciclo real ainda precisa de validação de ponta a ponta com autorização UAC e, quando exigido pelo Windows, reinicialização. A detecção por IPC e a presença/hash do instalador embutido podem ser verificadas sem alterar o sistema.


## Dispositivo vinculado à abertura e ao fechamento

Um gerenciador elevado é iniciado junto com a janela e permanece autorizado durante a abertura da aplicação. Ele instala o pacote assinado se necessário, reativa a coleção HID do vJoy 1 e verifica a configuração esperada. Uma configuração já adequada é preservada, sem recriar o dispositivo em cada abertura.

Ao fechar, a interface aguarda a parada do feeder e do FFB; depois o gerenciador desativa **apenas a coleção HID do vJoy 1**. O driver permanece instalado para a próxima abertura. A desativação persiste após reiniciar o Windows e a aplicação reativa sua coleção na próxima execução. Se a janela encerrar inesperadamente, o gerenciador detecta a saída do processo e tenta a mesma limpeza. Ele não desliga globalmente o serviço vJoy ou seus outros slots.

A coleção é identificada pelos atributos VID/PID conhecidos, pelo relatório de entrada 1 e pela cadeia de dispositivos que usa o serviço vjoy. Não são usados números ROOT/HIDCLASS fixos. A identidade fica em `%PROGRAMDATA%/ForzaNextWheel/Backups/device-one.json`. Não há elevação adicional ao fechar: o gerenciador autorizado na abertura faz essa operação. A comunicação aceita somente o usuário atual e administradores, confere os PIDs em ambas as pontas e não oferece comandos de shell arbitrários.

Para transmitir os controles físicos, use **Iniciar feeder** depois de mapear os dispositivos. A ativação automática do driver não inicia testes de torque. Se outro programa estiver usando o vJoy 1, a operação é recusada com diagnóstico em vez de desativar à força a sessão alheia.


## Versão 2.1 — perfis, calibração e diagnóstico

- **Perfis por jogo:** escolha o perfil na barra lateral com a sessão parada. Gerenciar perfis permite criar, duplicar, renomear, importar, exportar e excluir. Cada perfil guarda mapeamento, calibração e FFB. A seleção é manual; iniciar um processo de jogo não troca o perfil automaticamente. A configuração existente é copiada na primeira abertura para `%LOCALAPPDATA%/ForzaNextWheel/Profiles`, sem apagar o arquivo anterior.
- **Calibração:** em Controles, mapeie um eixo e clique em Calibrar. Percorra os limites, confirme o centro do volante ou o repouso do pedal e confira a saída. A leitura ocorre em um processo isolado, sem adquirir vJoy e sem criar efeitos de motor. Pedais detectam inversão pela posição de repouso. Cancelar mantém a calibração anterior.
- **Monitor de FFB:** exibe pacotes por segundo/total, efeitos ativos e tipos, idade do último pacote, saturação da soma da força constante e status/erros da sessão. Os valores são comandos, não uma medição do torque físico. Efeitos periódicos/condicionais não entram no cálculo de saturação da força constante.
- **USB:** Diagnóstico mostra os hubs externos no caminho de cada controlador HID de jogo e recomenda conexão direta quando necessário. A consulta é somente leitura e não bloqueia sessões por haver hubs.
- **Desempenho:** o worker utiliza um timer de alta resolução por processo, com fallback, sem alterar o período global dos timers do Windows. Diagnóstico mede média, p95, p99 e maior intervalo do agendador por três segundos; a sessão mostra a taxa real do processamento de entradas. `dotnet run --project src/ForzaNext.Cli -- benchmark-loop 3 --compare` compara as esperas. A medição sintética não representa latência física nem garante 1.000 relatórios USB/s.
- **Recuperação:** cada gravação mantém `.bak`. Perfis e catálogo corrompidos podem ser recuperados automaticamente quando há backup válido. O botão Recuperar backup preserva a versão substituída. Excluir arquiva o perfil em `Profiles/Archive`.
- **Distribuição:** `scripts/build-release.ps1` gera instalador com atalhos e pacote portátil com .NET incluído. Instalações novas começam sem mapeamentos; configurações existentes do usuário são preservadas. O app impede duas instâncias simultâneas. A atualização local em Diagnóstico valida produto, versão, nome, tamanho e SHA-256, solicita confirmação e fecha o dispositivo antes de abrir o instalador. Não há atualização online configurada. Veja [installer/README.md](installer/README.md).

Verificação da calibração sem força: `scripts/check-worker.ps1 -UiExecutable <exe-ou-dll> -ProfilePath <perfil> -Capture` consulta entradas físicas e encerra o worker. Nunca envia teste de motor.


## Correção 2.1.1 — contrato binário do FFB

O SDK Brunner vJoy 2.2.2 usa `FFB_EFF_REPORT` com 28 bytes: StartDelay em 14,
Gain em 16, AxesEnabledDirection em 18, Polar em 20 e direções de 16 bits em 24/26.
A declaração anterior de 24 bytes era incompatível e permitia que o decodificador
nativo sobrescrevesse memória. Corrigidos tamanho, offsets e propagação do atraso.
A inicialização agora verifica o layout usando um pacote sintético e uma área de saída
com sentinelas antes de adquirir o dispositivo. DLLs com layout incompatível são
recusadas com diagnóstico. `dotnet run --project src/ForzaNext.Cli -- verify-vjoy-abi`
executa a mesma verificação sem abrir dispositivos nem enviar força.

Em 05/11/2026, o dump do worker mostrou corrupção do frame da callback do vJoy.
O dump do FH6 mostrou falha em `pid!PID_EffectOperation`, chamada por
`PID_DestroyEffect`, `PID_DownloadEffect` e `Windows.Gaming.Input`.
A correção resolve a incompatibilidade demonstrada no nosso processo; confirmar
que ela também elimina a queda do jogo exige repetir a sessão no FH6.


## Correção 2.1.2 — sobrecarga ao entrar na corrida

Os logs de 05/11/2026 registraram `Fila FFB excedeu o limite` ao passar do menu
para a corrida. Atualizações de parâmetros passaram a ser consolidadas pelo tipo,
bloco e eixo, mantendo o valor mais recente antes do próximo comando de ciclo de
vida. Start, stop, solo, free, reset e mudanças de tipo delimitam a consolidação.
A fila permanece limitada e uma sobrecarga de comandos de ciclo de vida continua
produzindo erro explícito, em vez de descartar silenciosamente uma parada.

O redutor aplica uma vez por bloco ao final de cada lote. Parâmetros não iniciam
um efeito parado; stop/reset/desativação impedem a aplicação de força pendente.
A ponte DirectInput envia somente os campos alterados, sem reiniciar efeitos em
atualizações comuns. O monitor mostra itens pendentes e atualizações consolidadas.
Testes incluem rajadas de 400 mil atualizações, concorrência, limites da fila,
parada/reset, reutilização das posições da fila e seleção de escritas nativas.
O comportamento físico e a transição no jogo precisam ser conferidos na sessão real.
