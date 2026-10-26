(() => {
  'use strict';
  const page = location.pathname.split('/').pop();
  const pages = {
    status: ['Status', 'Acompanhe a saúde do serviço e a qualidade das capturas.'],
    devices: ['Dispositivos', 'Veja os scanners detectados e as configurações do driver.'],
    logs: ['Logs', 'Investigue digitalizações e problemas do serviço local.']
  };
  if (!pages[page]) return;
  const $ = id => document.getElementById(id);
  const set = (id, value) => { $(id).textContent = value ?? '—'; };
  const dateTime = value => value ? new Date(value).toLocaleString('pt-BR') : '—';
  const size = value => value == null ? '—' : value >= 1048576 ? `${(value / 1048576).toFixed(1)} MB` : `${Math.round(value / 1024)} KB`;
  let allLogs = [];
  let busy = false;
  let startupState = null;

  $('page-title').textContent = pages[page][0];
  $('page-description').textContent = pages[page][1];
  $(`${page}-page`).hidden = false;
  document.querySelector(`[data-page="${page}"]`)?.classList.add('active');
  setInterval(() => set('clock', new Date().toLocaleString('pt-BR')), 1000);
  set('clock', new Date().toLocaleString('pt-BR'));
  $('refresh').addEventListener('click', () => load());

  async function fetchJson(path) {
    const response = await fetch(path, { cache: 'no-store' });
    if (!response.ok) throw new Error(`Serviço respondeu HTTP ${response.status}.`);
    return response.json();
  }

  function notice(message) {
    $('notice').textContent = message || '';
    $('notice').hidden = !message;
  }

  async function load() {
    if (busy) return;
    busy = true;
    try {
      notice('');
      if (page === 'status') renderStatus(await fetchJson('/api/dashboard/status'));
      if (page === 'devices') renderDevices(await fetchJson('/api/dashboard/devices'));
      if (page === 'logs') renderLogsResponse(await fetchJson(`/api/dashboard/logs?lines=${$('log-lines').value}`));
    } catch (error) {
      notice(`Não foi possível atualizar: ${error.message}`);
    } finally {
      busy = false;
    }
  }

  function renderStatus(data) {
    const scan = data.scan || {};
    set('version', data.version);
    set('scan-requested', scan.requested);
    set('scan-succeeded', scan.succeeded);
    set('scan-failed', scan.failed);
    set('scan-active', scan.active);
    set('scan-dpi', scan.lastDpiX && scan.lastDpiY ? `${scan.lastDpiX} × ${scan.lastDpiY} DPI` : 'Aguardando scan');
    set('last-at', dateTime(scan.lastAt));
    set('last-format', scan.lastFormat);
    set('last-dimensions', scan.lastWidth && scan.lastHeight ? `${scan.lastWidth} × ${scan.lastHeight} px` : '—');
    set('last-size', size(scan.lastOutputBytes));
    set('machine', data.machine);
    set('os', data.os);
    set('architecture', data.architecture);
    set('dotnet', data.dotnet);
    set('started', `${dateTime(data.startedAt)} · ${Math.floor(data.uptimeSeconds / 60)} min ativo`);
    set('process', `PID ${data.processId} · ${size(data.memoryBytes)}`);
    const pill = $('last-result');
    pill.textContent = scan.lastResult || 'Sem digitalização';
    pill.className = `pill ${scan.lastResult === 'Concluída' ? 'good' : scan.lastResult === 'Falhou' ? 'bad' : ''}`;
    $('last-error').hidden = !scan.lastError;
    set('last-error', scan.lastError);
    const wia = $('wia-pill');
    wia.textContent = data.wiaAvailable ? 'WIA disponível' : 'WIA indisponível';
    wia.className = `pill ${data.wiaAvailable ? 'good' : 'bad'}`;
    renderStartup(data.startup);
  }

  function renderStartup(startup) {
    startupState = startup || null;
    const pill = $('startup-pill');
    const toggle = $('startup-toggle');
    const repair = $('startup-repair');
    if (!startup) {
      pill.textContent = 'Indisponível';
      set('startup-description', 'Não foi possível consultar a inicialização automática.');
      toggle.disabled = true;
      repair.hidden = true;
      return;
    }
    const current = startup.configured && startup.pointsToCurrentExe;
    pill.textContent = current ? 'Ativado' : startup.configured ? 'Caminho antigo' : 'Desativado';
    pill.className = `pill ${current ? 'good' : startup.configured ? 'bad' : ''}`;
    set('startup-path', startup.executablePath);
    set('startup-description', current
      ? 'O conector abrirá automaticamente quando este usuário entrar no Windows.'
      : startup.configured
        ? 'Existe uma configuração antiga. Atualize o caminho para iniciar esta versão do conector.'
        : 'O conector só inicia quando você o abre manualmente.');
    toggle.disabled = false;
    toggle.textContent = startup.configured ? 'Desativar inicialização automática' : 'Ativar inicialização automática';
    repair.hidden = !startup.configured || current;
  }

  async function updateStartup(enabled) {
    const toggle = $('startup-toggle');
    const repair = $('startup-repair');
    toggle.disabled = true;
    repair.disabled = true;
    set('startup-feedback', 'Salvando configuração...');
    try {
      const response = await fetch('/api/dashboard/startup', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'X-Scanner-Dashboard': '1' },
        body: JSON.stringify({ enabled })
      });
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      renderStartup(await response.json());
      set('startup-feedback', enabled ? 'Inicialização automática ativada.' : 'Inicialização automática desativada.');
    } catch (error) {
      set('startup-feedback', `Falha ao salvar: ${error.message}`);
    } finally {
      toggle.disabled = false;
      repair.disabled = false;
    }
  }

  function element(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text != null) node.textContent = String(text);
    return node;
  }

  function renderDevices(data) {
    const list = $('device-list');
    list.replaceChildren();
    set('device-count', `${data.count || 0} dispositivo${data.count === 1 ? '' : 's'}`);
    if (!data.available) notice(data.error || 'O serviço WIA não está disponível.');
    if (!data.devices?.length) {
      list.append(element('div', 'empty', data.available ? 'Nenhum dispositivo encontrado pelo Windows.' : 'Não foi possível consultar os dispositivos.'));
      return;
    }
    data.devices.forEach(device => {
      const card = element('article', 'device-card');
      const head = element('div', 'device-head');
      const title = element('div', 'device-name');
      title.append(element('span', 'device-icon', '▣'));
      const copy = element('div');
      copy.append(element('h3', '', device.name || 'Scanner sem nome'));
      copy.append(element('p', 'sub', device.id || 'ID indisponível'));
      title.append(copy);
      head.append(title, element('span', `pill ${device.type === 1 ? 'good' : ''}`, device.type === 1 ? 'Scanner' : `Tipo ${device.type}`));
      card.append(head);
      const stats = element('div', 'device-stats');
      const values = [
        ['Resolução horizontal', device.horizontalDpi ? `${device.horizontalDpi} DPI` : 'Não informada'],
        ['Resolução vertical', device.verticalDpi ? `${device.verticalDpi} DPI` : 'Não informada'],
        ['Fabricante', device.properties?.Manufacturer || device.properties?.Description || 'Não informado']
      ];
      values.forEach(([label, value]) => {
        const box = element('div', 'device-stat');
        box.append(element('span', '', label), element('strong', '', value));
        stats.append(box);
      });
      card.append(stats);
      if (device.type === 1) {
        const actions = element('div', 'device-actions');
        const test = element('button', 'button button-outline', '▣ Digitalizar PDF de teste');
        test.type = 'button';
        const feedback = element('span', 'device-feedback', 'Coloque um documento no scanner antes do teste.');
        test.addEventListener('click', async () => {
          test.disabled = true;
          test.textContent = 'Digitalizando...';
          feedback.textContent = 'Aguardando o scanner.';
          try {
            const response = await fetch(`/scan/pdf?deviceId=${encodeURIComponent(device.id)}`, { method: 'POST' });
            if (!response.ok) throw new Error(`HTTP ${response.status}`);
            const url = URL.createObjectURL(await response.blob());
            const link = document.createElement('a');
            link.href = url;
            link.download = `teste-scanner-${Date.now()}.pdf`;
            document.body.append(link);
            link.click();
            link.remove();
            setTimeout(() => URL.revokeObjectURL(url), 60000);
            feedback.textContent = 'PDF de teste gerado. Confira a resolução na página Status.';
          } catch (error) {
            feedback.textContent = `Falha no teste: ${error.message}. Veja os Logs.`;
          } finally {
            test.disabled = false;
            test.textContent = '▣ Digitalizar PDF de teste';
          }
        });
        actions.append(test, feedback);
        card.append(actions);
      }
      if (device.detailsError) card.append(element('p', 'error-text', `Detalhes indisponíveis: ${device.detailsError}`));
      const details = element('details');
      details.append(element('summary', '', 'Ver propriedades do dispositivo e do scanner'));
      const table = element('div', 'property-table');
      const properties = { ...device.properties, ...device.scanProperties };
      const entries = Object.entries(properties).sort(([a], [b]) => a.localeCompare(b, 'pt-BR'));
      if (!entries.length) {
        table.append(element('div', '', 'Propriedades'), element('div', '', 'Não disponibilizadas pelo driver'));
      } else {
        entries.forEach(([name, value]) => table.append(element('div', '', name), element('div', '', value)));
      }
      details.append(table);
      card.append(details);
      list.append(card);
    });
  }

  function renderLogsResponse(data) {
    allLogs = data.lines || [];
    set('log-updated', `Atualizado: ${dateTime(data.updatedAt)}`);
    set('log-path', data.path);
    renderLogs();
  }

  function renderLogs() {
    const q = $('log-filter').value.trim().toLocaleLowerCase('pt-BR');
    const lines = q ? allLogs.filter(line => line.toLocaleLowerCase('pt-BR').includes(q)) : allLogs;
    set('log-count', `${lines.length} de ${allLogs.length} linhas visíveis`);
    const list = $('log-list');
    list.replaceChildren();
    if (!lines.length) {
      list.append(element('div', 'empty', q ? 'Nenhuma linha corresponde ao filtro.' : 'Ainda não existem logs.'));
      return;
    }
    const wasNearBottom = list.scrollTop + list.clientHeight >= list.scrollHeight - 80;
    const fragment = document.createDocumentFragment();
    lines.forEach(line => {
      const level = /erro|falh|exception|não aceitou/i.test(line) ? 'error' : /aviso|fallback|indisponível/i.test(line) ? 'warning' : /sucesso|concluído|efetiva=/i.test(line) ? 'success' : '';
      fragment.append(element('div', `log-row ${level}`, line));
    });
    list.append(fragment);
    if (wasNearBottom) list.scrollTop = list.scrollHeight;
  }

  if (page === 'logs') {
    $('log-filter').addEventListener('input', renderLogs);
    $('log-lines').addEventListener('change', load);
    setInterval(() => { if ($('log-auto').checked && !document.hidden) load(); }, 6000);
  } else if (page === 'status') {
    $('startup-toggle').addEventListener('click', () => updateStartup(!startupState?.configured));
    $('startup-repair').addEventListener('click', () => updateStartup(true));
    setInterval(() => { if (!document.hidden) load(); }, 10000);
  }
  load();
})();
