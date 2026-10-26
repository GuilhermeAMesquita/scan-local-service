# Scanner local — Regulariza Farma

O serviço fica em `http://127.0.0.1:4545` e mantém os endpoints usados pelo app (`/health`, `/devices`, `/scan/pdf` e `/scan/png`). A interface de apoio é aberta com dois cliques no ícone da bandeja do Windows, pelo menu **Abrir painel**, ou diretamente em `http://127.0.0.1:4545/ui/status`.

O painel tem três páginas:

- **Status:** versão, ambiente, WIA, contadores da sessão, último scan e resolução efetiva.
- **Dispositivos:** scanners vistos pelo WIA e propriedades que o driver expõe, sem alterar suas configurações ao consultar; botão de digitalização de teste em PDF.
- **Logs:** eventos recentes, busca, atualização automática e download do arquivo completo.

O log fica em `%LOCALAPPDATA%\RegularizaFarma\Scanner\scanner-log.txt`. Ao iniciar a nova versão, o serviço tenta copiar o log antigo que ficava ao lado do executável. A interface e a API de diagnóstico aceitam apenas acesso local; o serviço continua vinculado a `127.0.0.1`.

## Teste em Windows

1. Saia da versão antiga pelo ícone da bandeja e descompacte o pacote novo, preservando a pasta `LatoFont` junto do `.exe`.
2. Execute `ScanLocalService.exe` e abra `http://127.0.0.1:4545/ui/status`.
3. Confirme a versão **1.0.9.0** e se o scanner aparece em **Dispositivos**.
4. Faça uma captura de teste pelo botão em **Dispositivos** ou pelo Regulariza Popular; confira a resolução e as dimensões em **Status**. Para um A4 a 300 DPI, a imagem deve ter aproximadamente 2480 × 3508 pixels. O tamanho exato varia pelo driver e pela área selecionada.
5. Se a resolução ficar abaixo de 200 DPI ou houver falha, abra **Logs**, baixe o arquivo e verifique as mensagens de WIA e as dimensões capturadas.

O build automatizado em Windows compila o conector, inicia o `.exe`, confirma `/health` e as três páginas do painel, e publica um pacote `win-x64`. O teste com um scanner físico deve ser feito antes da distribuição geral.
