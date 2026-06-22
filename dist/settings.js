// ccswitcher Settings Window
// Handles UI interactions and Tauri command invocations for account management,
// proxy settings, and import functionality.

(function() {
  'use strict';

  // === State ===
  let accounts = [];
  let state = null;
  let accountToDelete = null;

  // === DOM Elements ===
  const els = {
    message: document.getElementById('message'),
    accountsList: document.getElementById('accountsList'),
    addTokenAccountBtn: document.getElementById('addTokenAccountBtn'),
    proxyForm: document.getElementById('proxyForm'),
    proxyEnabled: document.getElementById('proxyEnabled'),
    proxyUrl: document.getElementById('proxyUrl'),
    noProxy: document.getElementById('noProxy'),
    importBtn: document.getElementById('importBtn'),
    // Account modal
    accountModal: document.getElementById('accountModal'),
    modalTitle: document.getElementById('modalTitle'),
    closeModal: document.getElementById('closeModal'),
    accountForm: document.getElementById('accountForm'),
    accountId: document.getElementById('accountId'),
    accountName: document.getElementById('accountName'),
    baseUrl: document.getElementById('baseUrl'),
    authKind: document.getElementById('authKind'),
    token: document.getElementById('token'),
    extraEnvContainer: document.getElementById('extraEnvContainer'),
    addExtraEnvBtn: document.getElementById('addExtraEnvBtn'),
    cancelBtn: document.getElementById('cancelBtn'),
    // Import modal
    importModal: document.getElementById('importModal'),
    closeImportModal: document.getElementById('closeImportModal'),
    importForm: document.getElementById('importForm'),
    importName: document.getElementById('importName'),
    cancelImportBtn: document.getElementById('cancelImportBtn'),
    // Delete modal
    deleteModal: document.getElementById('deleteModal'),
    closeDeleteModal: document.getElementById('closeDeleteModal'),
    deleteAccountName: document.getElementById('deleteAccountName'),
    cancelDeleteBtn: document.getElementById('cancelDeleteBtn'),
    confirmDeleteBtn: document.getElementById('confirmDeleteBtn')
  };

  // === Tauri API ===
  const invoke = window.__TAURI__?.core?.invoke || window.invoke;
  const tauri = window.__TAURI__;

  // === Helper Functions ===

  function showMessage(text, type = 'success') {
    els.message.textContent = text;
    els.message.className = `message ${type}`;
    setTimeout(() => {
      els.message.classList.add('hidden');
    }, 5000);
  }

  function hideMessage() {
    els.message.classList.add('hidden');
  }

  function showError(err) {
    console.error('Error:', err);
    let message = 'An error occurred';
    if (typeof err === 'string') {
      message = err;
    } else if (err?.message) {
      message = err.message;
    } else if (err?.kind) {
      message = `Error: ${err.kind}`;
    }
    showMessage(message, 'error');
  }

  function showModal(modal) {
    modal.classList.remove('hidden');
  }

  function hideModal(modal) {
    modal.classList.add('hidden');
  }

  // === API Calls ===

  async function loadState() {
    try {
      const result = await invoke('get_state');
      state = result;
      accounts = result.accounts || [];
      renderAccounts();
      loadProxy();
    } catch (err) {
      showError(err);
    }
  }

  async function loadProxy() {
    try {
      const proxy = await invoke('get_proxy');
      els.proxyEnabled.checked = proxy.enabled || false;
      els.proxyUrl.value = proxy.url || '';
      els.noProxy.value = proxy.no_proxy || '';
    } catch (err) {
      showError(err);
    }
  }

  async function switchAccount(accountId) {
    try {
      hideMessage();
      await invoke('switch_account', { accountId });
      await loadState(); // Reload to get updated state
      showMessage('Switched account successfully');
    } catch (err) {
      showError(err);
    }
  }

  async function saveTokenAccount(data) {
    try {
      hideMessage();
      const isEdit = !!data.id;
      let result;

      if (isEdit) {
        // For editing, we only send fields that can change
        const updateParams = {
          id: data.id,
          name: data.name,
          base_url: data.base_url || null,
          extra_env: data.extra_env || {}
        };
        // Only include secret if provided
        if (data.secret) {
          updateParams.secret = data.secret;
        }
        result = await invoke('update_account', { params: updateParams });
      } else {
        // For new accounts, we require secret
        const addParams = {
          name: data.name,
          base_url: data.base_url || null,
          auth_kind: data.auth_kind,
          secret: data.secret,
          extra_env: data.extra_env || {}
        };
        result = await invoke('add_token_account', { params: addParams });
      }

      hideModal(els.accountModal);
      await loadState();
      showMessage(isEdit ? 'Account updated successfully' : 'Account added successfully');
      return result;
    } catch (err) {
      showError(err);
      throw err;
    }
  }

  async function deleteAccount(accountId) {
    try {
      hideMessage();
      await invoke('delete_account', { accountId });
      hideModal(els.deleteModal);
      await loadState();
      showMessage('Account deleted successfully');
    } catch (err) {
      showError(err);
    }
  }

  async function importAccount(name) {
    try {
      hideMessage();
      const result = await invoke('import_current', { name: name || null });

      hideModal(els.importModal);
      await loadState();

      if (result.warning) {
        showMessage(`Account imported. Note: ${result.warning}`, 'warning');
      } else {
        showMessage('Account imported successfully');
      }
    } catch (err) {
      showError(err);
    }
  }

  async function setProxyEnabled(enabled) {
    try {
      hideMessage();
      await invoke('set_proxy_enabled', { enabled });
      await loadState(); // Reload to get updated state
      showMessage(enabled ? 'Proxy enabled' : 'Proxy disabled');
    } catch (err) {
      showError(err);
      // Revert checkbox on error
      els.proxyEnabled.checked = !enabled;
    }
  }

  // === Rendering ===

  function renderAccounts() {
    if (!accounts || accounts.length === 0) {
      els.accountsList.innerHTML = '<p class="empty-state">No accounts yet. Click "Add Token Account" or "Import Current Login" to get started.</p>';
      return;
    }

    const html = accounts.map(acc => {
      const isActive = state?.active_account_id === acc.id;
      const typeLabel = acc.account_type === 'anthropic_oauth' ? 'OAuth' : 'Token';
      const activeClass = isActive ? 'active' : '';
      const activeIndicator = isActive ? '<span class="active-indicator">&check;</span>' : '';

      return `
        <div class="account-item ${activeClass}">
          <div class="account-info">
            <div class="account-name">${activeIndicator}${escapeHtml(acc.name)}</div>
            <div class="account-type">${typeLabel}</div>
          </div>
          <div class="account-actions">
            ${!isActive ? `<button class="btn btn-secondary" data-action="switch" data-id="${acc.id}">Switch</button>` : '<span style="color:#0066cc; font-size:12px; margin-right:4px;">Current</span>'}
            <button class="btn btn-secondary" data-action="edit" data-id="${acc.id}">Edit</button>
            <button class="btn btn-danger" data-action="delete" data-id="${acc.id}">Delete</button>
          </div>
        </div>
      `;
    }).join('');

    els.accountsList.innerHTML = html;

    // Attach event listeners to buttons
    els.accountsList.querySelectorAll('button[data-action]').forEach(btn => {
      btn.addEventListener('click', handleAccountAction);
    });
  }

  function escapeHtml(text) {
    const div = document.createElement('div');
    div.textContent = text;
    return div.innerHTML;
  }

  // === Extra Env Handling ===

  function addExtraEnvRow(key = '', value = '') {
    const row = document.createElement('div');
    row.className = 'extra-env-row';
    row.innerHTML = `
      <input type="text" placeholder="Key" value="${escapeHtml(key)}" class="env-key" />
      <input type="text" placeholder="Value" value="${escapeHtml(value)}" class="env-value" />
      <button type="button" data-action="remove-env">&times;</button>
    `;

    row.querySelector('button[data-action="remove-env"]').addEventListener('click', () => {
      row.remove();
    });

    els.extraEnvContainer.appendChild(row);
  }

  function getExtraEnvValues() {
    const rows = els.extraEnvContainer.querySelectorAll('.extra-env-row');
    const result = {};
    rows.forEach(row => {
      const key = row.querySelector('.env-key').value.trim();
      const value = row.querySelector('.env-value').value.trim();
      if (key) {
        result[key] = value;
      }
    });
    return result;
  }

  function setExtraEnvValues(extraEnv) {
    els.extraEnvContainer.innerHTML = '';
    if (extraEnv && typeof extraEnv === 'object') {
      Object.entries(extraEnv).forEach(([key, value]) => {
        addExtraEnvRow(key, value);
      });
    }
  }

  // === Event Handlers ===

  function handleAccountAction(e) {
    const action = e.target.dataset.action;
    const id = e.target.dataset.id;
    const account = accounts.find(a => a.id === id);

    if (!account) return;

    switch (action) {
      case 'switch':
        switchAccount(id);
        break;
      case 'edit':
        openEditAccountModal(account);
        break;
      case 'delete':
        openDeleteModal(account);
        break;
    }
  }

  function openAddAccountModal() {
    els.modalTitle.textContent = 'Add Token Account';
    els.accountId.value = '';
    els.accountName.value = '';
    els.baseUrl.value = '';
    els.authKind.value = 'auth_token';
    els.token.value = '';
    els.token.required = true;
    els.token.placeholder = 'sk-ant-...';
    setExtraEnvValues({});
    showModal(els.accountModal);
  }

  function openEditAccountModal(account) {
    els.modalTitle.textContent = 'Edit Account';
    els.accountId.value = account.id;
    els.accountName.value = account.name;
    els.baseUrl.value = account.base_url || '';
    els.token.value = '';
    els.token.required = false;
    els.token.placeholder = 'Leave empty to keep existing token';

    // For token accounts, set auth_kind; for OAuth, hide token field
    if (account.account_type === 'token') {
      els.authKind.value = account.auth_kind || 'auth_token';
      els.authKind.disabled = false;
      els.token.closest('.form-group').style.display = '';
    } else {
      // OAuth account - can't change auth_kind, token field is irrelevant
      els.authKind.value = 'auth_token';
      els.authKind.disabled = true;
      els.token.closest('.form-group').style.display = 'none';
    }

    setExtraEnvValues(account.extra_env || {});
    showModal(els.accountModal);
  }

  function openDeleteModal(account) {
    accountToDelete = account.id;
    els.deleteAccountName.textContent = account.name;
    showModal(els.deleteModal);
  }

  function handleAccountSubmit(e) {
    e.preventDefault();

    const data = {
      id: els.accountId.value || null,
      name: els.accountName.value.trim(),
      base_url: els.baseUrl.value.trim() || null,
      auth_kind: els.authKind.value,
      secret: els.token.value,
      extra_env: getExtraEnvValues()
    };

    if (!data.name) {
      showMessage('Account name is required', 'error');
      return;
    }

    // For new accounts, secret is required
    if (!data.id && !data.secret) {
      showMessage('Token is required for new accounts', 'error');
      return;
    }

    saveTokenAccount(data).catch(() => {
      // Error already shown in saveTokenAccount
    });
  }

  function handleProxySubmit(e) {
    e.preventDefault();

    // Update the proxy settings in state
    const wasEnabled = els.proxyEnabled.checked;
    const newEnabled = els.proxyEnabled.checked;

    if (wasEnabled !== newEnabled) {
      setProxyEnabled(newEnabled);
    } else {
      showMessage('Proxy settings saved');
    }
  }

  function handleImportSubmit(e) {
    e.preventDefault();
    const name = els.importName.value.trim();
    importAccount(name);
    els.importName.value = '';
  }

  // === Initialization ===

  function init() {
    // Load initial state
    loadState();

    // Account buttons
    els.addTokenAccountBtn.addEventListener('click', openAddAccountModal);
    els.importBtn.addEventListener('click', () => {
      els.importName.value = '';
      showModal(els.importModal);
    });

    // Modal close buttons
    els.closeModal.addEventListener('click', () => hideModal(els.accountModal));
    els.closeImportModal.addEventListener('click', () => hideModal(els.importModal));
    els.closeDeleteModal.addEventListener('click', () => hideModal(els.deleteModal));

    // Cancel buttons
    els.cancelBtn.addEventListener('click', () => hideModal(els.accountModal));
    els.cancelImportBtn.addEventListener('click', () => hideModal(els.importModal));
    els.cancelDeleteBtn.addEventListener('click', () => hideModal(els.deleteModal));

    // Delete confirmation
    els.confirmDeleteBtn.addEventListener('click', () => {
      if (accountToDelete) {
        deleteAccount(accountToDelete);
        accountToDelete = null;
      }
    });

    // Forms
    els.accountForm.addEventListener('submit', handleAccountSubmit);
    els.proxyForm.addEventListener('submit', handleProxySubmit);
    els.importForm.addEventListener('submit', handleImportSubmit);

    // Extra env
    els.addExtraEnvBtn.addEventListener('click', () => addExtraEnvRow());

    // Proxy enabled checkbox - immediate toggle
    els.proxyEnabled.addEventListener('change', () => {
      setProxyEnabled(els.proxyEnabled.checked);
    });

    // Close modals on backdrop click
    [els.accountModal, els.importModal, els.deleteModal].forEach(modal => {
      modal.addEventListener('click', (e) => {
        if (e.target === modal) {
          hideModal(modal);
        }
      });
    });
  }

  // Start when DOM is ready
  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
})();
