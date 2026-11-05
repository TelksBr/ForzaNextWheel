# Correções da análise de 04/11/2026

O código foi atualizado para tratar os 24 achados de `ANALISE-2026-11-04.md`. Este registro descreve o que foi implementado; não equivale a homologação física com ODrive/Forza.

1. **ABI vJoy:** primeiro bloco de botões antes dos POVs, alinhamento explícito e regressões de offsets/tamanhos/bytes neutros. Também corrigida a calling convention do callback e a assinatura WORD de DriverMatch.
2. **FFB bloqueado:** removida a referência `_ffbDev` nunca inicializada. Pacotes são copiados no callback e processados pelo proprietário dos dispositivos.
3. **Estado por efeito:** redutor com estado independente por EffectBlockIndex; mensagens intercaladas não compartilham tipo, gain ou parâmetros.
4. **Protocolo:** start, solo, stop, duração, iterações, gain, direção, envelope, pause/continue, reset e liberação de blocos; parâmetros sozinhos não iniciam torque. Efeitos não suportados pelo hardware são ignorados com diagnóstico de FFB parcial. Custom/sample data permanece fora do subconjunto suportado, documentado no README.
5. **Parada:** envio de relatório neutro e liberação do dispositivo virtual.
6. **Perda de leitura:** invalidação completa do buffer; interrupção da sessão na primeira leitura inválida, com instrução de reconectar e reiniciar.
7. **Reset concorrente:** interface aguarda a parada antes de atualizar dispositivos. Operações nativas da sessão estão em uma única thread.
8. **Lifecycle:** cancelamento da inicialização e espera pela finalização antes do fechamento/descarte; cleanup também ocorre na falha de startup.
9. **Aquisição exclusiva:** handles físicos liberados ao terminar a sessão; detecção usa seu próprio escopo e reporta falhas de aquisição.
10. **Timeout abandonado:** removidas as threads descartadas após 600 ms e o retry que compartilhava parâmetros. Criação é serial e todo efeito tem dono. Uma chamada nativa que nunca retorna ainda depende do driver; a aplicação aguarda em vez de usar/dispor handles concorrentes. Isolamento em processo separado é uma evolução necessária para recuperação garantida nesse cenário.
11. **Estado verdadeiro:** entradas ativas, FFB disponível/parcial/indisponível e falhas de vJoy são diferenciados. Falhas de polling e feeding encerram a sessão.
12. **Perfis concorrentes:** snapshots independentes por sessão; edição/salvamento/reload/detecção bloqueados durante operações incompatíveis.
13. **Teste do motor:** teste finito controlado pelo worker, cancelado junto com a sessão e sem continuações que reativem torque depois da parada concluída. Testes simultâneos são bloqueados.
14. **Desinstalação:** script usa somente o desinstalador oficial e verifica retorno; removidos INF/root IDs fixos, purga manual de registro/Windows e exclusão de ferramentas do projeto. Nenhum desinstalador foi executado.
15. **Freio de mão nativo:** opcional, não associado à direção; eixos distintos e backup de registro antes de alterações.
16. **Inversões por dispositivo:** cada ação é atualizada no perfil que contém seu eixo, incluindo pedais e freio de mão USB separados.
17. **Persistência FFB:** inversão persistida, frequências/saturações/fase preservadas e carregadas na configuração de execução.
18. **Perfis confiáveis:** validação de GUIDs, nomes, ações duplicadas, índices e intervalos finitos; escrita temporária com flush e substituição atômica, mantendo `.bak`.
19. **D-pad:** respeita índice do POV físico, inicializa canais neutros e cancela direções digitais opostas; valida um POV contínuo configurado no vJoy quando usado.
20. **Distribuição:** caminhos de perfil/log em LocalAppData, perfil inicial copiado na publicação, x64 explícito na interface/CLI e dependência NuGet SharpDX 4.2.0. CLI substituída por validador documentado; utilitários de decompilação não integram o executável.
21. **Identidade/capacidades:** FFB detectado por DirectInput, destino explícito respeitado e reconciliação por produto apenas quando sem ambiguidade. Reconexão exige iniciar uma nova sessão; torque não retoma automaticamente.
22. **Frequência:** taxa e maior intervalo medidos na janela de um segundo; removida promessa de 1000 Hz garantidos. Estado de leitura é reutilizado e publicação de telemetria ocorre a 30 Hz.
23. **Repositório:** `.gitignore`, locks NuGet, SDK .NET 8 fixado por família, testes, CI Windows, instruções de build/publicação e referências de dependências/licenças. Os 160 arquivos gerados antes rastreados em bin/obj foram removidos somente do índice Git; os arquivos locais continuam no disco.
24. **Diagnóstico:** log centralizado com rotação e contexto; falha de sessão encerra recursos; paradas/liberações tentam cada efeito separadamente e tentam desabilitar os atuadores. AutoCenter é restaurado quando disponível.

## Validação

- Build Release: sem erros ou avisos.
- 38 testes automatizados: aprovados, sem hardware e sem envio de torque.
- Perfis: configuração inicial aprovada pela CLI.
- Scripts PowerShell: parsing sintático aprovado, sem execução dos scripts administrativos.
- Cópia limpa sem bin/obj/lib/DLLs locais: restore em modo locked, build, os 38 testes e publish win-x64 aprovados.
- Publicação: `artifacts/publish/ForzaNext.UI.exe`.
- Diff de fontes e índice: sem erros de whitespace.

A integração física com a instalação de vJoy, firmware e jogos ainda precisa ser testada. Não foram alterados drivers ou o registro desta máquina durante estas correções.


## Atualização — travamento ao iniciar com ODrive

O processo aberto foi inspecionado com debugger em modo não invasivo. A thread de dispositivos estava parada em `dinput8!CDIDev_Acquire` → `CDIDev_FFAcquire` → `pid!PID_SendForceFeedbackCommand` → `NtWriteFile`. Isso identifica uma espera nativa durante a aquisição exclusiva de FFB; a janela continuava respondendo, mas aguardava a inicialização sem limite.

O acesso nativo foi transferido para um processo auxiliar oculto, com HWND próprio. O supervisor usa timeout de 8 segundos na aquisição inicial, encerra o worker travado e tenta novamente sem FFB. O segundo startup, a parada e o encerramento também são limitados. Um watchdog verifica progresso real do feeder, além da conexão IPC. Testes cobrem bloqueio na aquisição, cancelamento, fallback, bloqueio na parada, ausência de progresso e preservação dos campos binários via IPC. A CI verifica também o startup real do executável auxiliar sem hardware.

No teste físico sem torque, a aquisição não exclusiva do ODrive, leitura de eixos e parada foram aprovadas. O perfil completo revelou outra exigência: o dispositivo vJoy 1 instalado possui menos botões que os 27 necessários pelo perfil. A mensagem agora calcula o total exigido pelo conjunto de perfis, informa a capacidade atual e orienta a habilitar 128 botões no Configure vJoy. Essa configuração é verificada antes de tentar FFB. O perfil do usuário não foi alterado nem foram reconfigurados drivers.


## Ciclo automático do dispositivo

A abertura agora solicita um gerenciador autorizado pelo Windows, que instala o vJoy assinado quando ausente, reativa o dispositivo 1 e aplica/confere a configuração de 128 botões, oito eixos, POV e FFB. O fechamento para primeiro o feeder e os efeitos, depois desativa somente a coleção HID cujo relatório de entrada é 1 e cuja cadeia usa o serviço vjoy. O driver permanece instalado. O gerenciador também observa a saída inesperada da janela.

Foram aprovados 66 testes e o build Release. Os testes novos cobrem o ciclo de vida, a parada durante startup, a reutilização da autorização, o isolamento das identidades físicas/outros slots, as estruturas HID e as permissões do pipe.

Na primeira execução real, o instalador foi iniciado e o Windows registrou que o serviço vjoy anterior estava pendente de exclusão. O log de instalação do Windows informou necessidade de reiniciar para os dispositivos que usam esse driver. A instalação ainda estava aguardando a atualização de drivers HID relacionados; não foi forçado o encerramento do instalador e não foi reiniciado o computador. A confirmação prática da ativação/desativação fica pendente dessa reinicialização.
