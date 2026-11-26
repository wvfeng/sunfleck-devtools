const endpoint = new URL('/__sunfleck/snapshot', location.href);
const options = new URL(location.href).searchParams;
if (options.get('sunfleckInspector') === '1') {
  const headers = { 'X-Sunfleck-Page': crypto.randomUUID() };
  const controls = document.getElementById('controls');
  const status = document.getElementById('status');
  let pick;
  let completed;
  let ready = false;
  let highlighted;

  async function send(action, value, selection) {
    const response = await fetch(endpoint, {
      method: 'POST',
      headers: { ...headers, 'Content-Type': 'application/json' },
      body: JSON.stringify({ action, value, selection }),
    });
    return response.ok;
  }

  function highlight(id) {
    if (highlighted?.dataset.node === id) return;
    highlighted?.removeAttribute('data-highlighted');
    highlighted = document.querySelector('[data-node="' + id + '"]');
    highlighted?.setAttribute('data-highlighted', '');
  }

  async function poll() {
    try {
      if (!ready) ready = await send('ready');
      const response = await fetch(endpoint, { headers, cache: 'no-store' });
      if (!response.ok) {
        ready = false;
      } else {
        const state = await response.json();
        pick = state.pick === completed ? undefined : state.pick;
        controls.toggleAttribute('data-picking', Boolean(pick));
        if (!pick) highlight(state.highlight);
        status.textContent = pick ? '点击元素拾取，Esc 取消' : '已连接';
      }
    } catch {
      ready = false;
      pick = undefined;
      highlight('0');
    } finally {
      setTimeout(poll, 125);
    }
  }

  function finishPick(node) {
    const request = pick;
    completed = request;
    pick = undefined;
    controls.removeAttribute('data-picking');
    highlight('0');
    void send('selected', request, node).then(sent => {
      if (!sent && completed === request) completed = undefined;
    }).catch(() => {
      if (completed === request) completed = undefined;
    });
  }

  controls.addEventListener('pointermove', event => {
    if (pick) highlight(event.target.closest('[data-node]')?.dataset.node || '0');
  });
  controls.addEventListener('pointerleave', () => {
    if (pick) highlight('0');
  });
  document.addEventListener('click', event => {
    if (!pick) return;
    event.preventDefault();
    event.stopImmediatePropagation();
    const node = event.target.closest('button, #template')?.dataset.node || '0';
    finishPick(node);
  }, true);
  document.addEventListener('keydown', event => {
    if (pick && event.key === 'Escape') {
      finishPick('0');
    }
  });
  void poll();
}
