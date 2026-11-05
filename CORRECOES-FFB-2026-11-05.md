# Correções de FFB — 05/11/2026

## Aplicadas

- Retirada a atenuação `sin(ângulo)` do ganho dos efeitos de um único atuador. Direções virtuais 0°/360° e direção cartesiana zero podiam zerar torque válido, mesmo com magnitude e ganho diferentes de zero.
- Parâmetros nativos do volante usam direção zero e magnitude assinada. A inversão configurada continua aplicada uma única vez ao sinal. Referência: exemplo Microsoft DirectInput FFConst (`SetDeviceForcesXY`, caso de um atuador), https://github.com/walbourn/directx-sdk-samples-reworked/blob/main/DirectInput/FFConst/ffconst.cpp.
- A telemetria de força constante passou a usar a magnitude assinada, sem multiplicar pelo sinal da direção nativa, que no caso de um atuador é zero.
- O diagnóstico de aquisição agora distingue criação do dispositivo, configuração cooperativa, faixas dos eixos, leitura/desativação de AutoCenter e chamada Acquire.
- O supervisor preserva a etapa em que o driver não respondeu. A execução em somente entradas não substitui essa causa pela mensagem genérica de ausência de destino ou FFB desabilitado no vJoy.
- A visão geral e a tela de FFB exibem a mesma causa de indisponibilidade. Os testes do motor continuam bloqueados se o FFB não estiver ativo.

## Limite da validação

Os testes automatizados verificam polaridade, ganho, direções polares/cartesianas zero, contingência, cancelamento e apresentação da falha, sem enviar força ao hardware.

O erro na conversão de direção ocorre depois da abertura do dispositivo. Ele não explica nem resolve, sozinho, a escrita USB bloqueada observada em `Acquire()`.

## Resultado da investigação física

- Reiniciar a base, reconectar o USB no caminho anterior e reiniciar a interface HID no Windows não eliminaram o bloqueio.
- O caminho original atravessava dois hubs USB `VID_214B&PID_7260` em cascata. A escrita de reset PID `0C 08` ficava pendente por mais de 8 segundos, inclusive na comparação da sequência original reconstruída.
- Após o usuário conectar o ODrive diretamente na placa-mãe, o pai PnP passou a ser `USB\ROOT_HUB30\5&19aa5c54&0&0`. A aquisição exclusiva passou imediatamente.
- A sessão completa da distribuição atual foi validada com o perfil real, volante e câmbio, usando `scripts/check-worker.ps1 -VerifyFfb`. A aquisição do volante levou aproximadamente 4 ms (11:17:44.805–11:17:44.809). O worker confirmou `HasFfb`, inicializou os efeitos físicos, publicou entradas e encerrou com código 0.
- A validação usou ganho mestre zero e não enviou comandos de teste do motor. Ela confirma a inicialização e o encerramento, não a sensação física das forças em um jogo.
- O histórico informado pelo usuário associa o início do problema a uma atualização do Windows. O resultado isola o caminho USB como condição do travamento neste computador, mas não identifica uma KB específica nem distingue uma regressão do Windows de uma incompatibilidade dos hubs/controlador.

O script `check-worker.ps1` aceita tanto o EXE quanto a DLL da aplicação. Sem opções continua sendo um teste de entradas sem FFB. `-VerifyFfb` exige um perfil explícito, inicializa FFB com ganho mestre zero, não envia testes de força e verifica força telemétrica zero antes de encerrar.

O repositório do firmware foi mantido sem alterações. Nenhum firmware foi gravado na ODESC v4.2 e nenhum efeito de teste foi reproduzido nesta correção.
