'use strict';
const { app, BrowserWindow, Menu, Tray, nativeImage, shell, dialog, session } = require('electron');
const { MAX_URL, isMaxUrl, isExternalUrl } = require('./navigation');
let window;
let tray;
let quitting = false;
const partition = 'persist:max';
const icon = nativeImage.createFromPath(require('node:path').join(__dirname, 'icon.png'));
async function openExternal(url) {
  if (isExternalUrl(url)) {
    try { await shell.openExternal(url); }
    catch { dialog.showErrorBox('Не удалось открыть ссылку', 'Откройте ссылку в браузере вручную.'); }
  }
}
function showWindow() { if (window) { window.show(); window.restore(); window.focus(); } }
function createWindow() {
  window = new BrowserWindow({
    width: 1100, height: 780, minWidth: 650, minHeight: 480,
    title: 'MAX Web Client', backgroundColor: '#ffffff', icon,
    webPreferences: { partition, nodeIntegration: false, contextIsolation: true, sandbox: true, webSecurity: true }
  });
  window.webContents.setWindowOpenHandler(({ url }) => { void openExternal(url); return { action: 'deny' }; });
  window.webContents.on('will-navigate', (event, url) => {
    if (!isMaxUrl(url)) { event.preventDefault(); void openExternal(url); }
  });
  window.webContents.on('will-redirect', (event, url) => {
    if (!isMaxUrl(url)) { event.preventDefault(); void openExternal(url); }
  });
  window.webContents.on('did-fail-load', (_event, code, _description, _url, isMainFrame) => {
    if (isMainFrame && code !== -3) dialog.showErrorBox('MAX недоступен', 'Проверьте подключение к интернету. Повторите загрузку через меню «MAX → Перезагрузить».');
  });
  window.on('close', event => { if (!quitting && tray) { event.preventDefault(); window.hide(); } });
  window.on('closed', () => { window = null; });
  void window.loadURL(MAX_URL).catch(() => {});
}
async function resetSession() {
  const result = await dialog.showMessageBox(window, {
    type: 'question', buttons: ['Отмена', 'Выйти'], defaultId: 0, cancelId: 0,
    message: 'Удалить локальную сессию MAX?', detail: 'При следующем входе понадобится повторная авторизация в MAX.'
  });
  if (result.response !== 1) return;
  const current = session.fromPartition(partition);
  await window.loadURL('about:blank');
  await current.clearStorageData();
  await current.clearCache();
  await window.loadURL(MAX_URL);
}
if (!app.requestSingleInstanceLock()) app.quit();
else {
  app.on('second-instance', showWindow);
  app.on('before-quit', () => { quitting = true; });
  app.whenReady().then(() => {
    const current = session.fromPartition(partition);
    const permitted = new Set(['notifications', 'media', 'clipboard-sanitized-write', 'fullscreen']);
    current.setPermissionCheckHandler((_contents, permission, origin) => isMaxUrl(origin) && permitted.has(permission));
    current.setPermissionRequestHandler((contents, permission, callback, details) => {
      callback(isMaxUrl(contents.getURL()) && isMaxUrl(details.requestingUrl) && permitted.has(permission));
    });
    Menu.setApplicationMenu(Menu.buildFromTemplate([
      { label: 'MAX', submenu: [
        { label: 'Показать окно', click: showWindow },
        { label: 'Перезагрузить', accelerator: 'Ctrl+R', click: () => window?.webContents.reload() },
        { label: 'Сбросить вход', click: () => resetSession().catch(() => dialog.showErrorBox('Ошибка', 'Не удалось удалить локальную сессию.')) },
        { type: 'separator' }, { role: 'quit', label: 'Выход' }
      ] },
      { label: 'Правка', submenu: [{ role: 'undo' }, { role: 'redo' }, { type: 'separator' }, { role: 'cut' }, { role: 'copy' }, { role: 'paste' }, { role: 'selectAll' }] },
      { label: 'Вид', submenu: [{ role: 'resetZoom' }, { role: 'zoomIn' }, { role: 'zoomOut' }, { role: 'togglefullscreen' }] }
    ]));
    createWindow();
    try {
      tray = new Tray(icon);
      tray.setToolTip('MAX Web Client');
      tray.setContextMenu(Menu.buildFromTemplate([{ label: 'Открыть MAX', click: showWindow }, { label: 'Выход', click: () => app.quit() }]));
      tray.on('double-click', showWindow);
    } catch { tray = null; }
    app.on('activate', () => { if (!window) createWindow(); else showWindow(); });
  });
  app.on('window-all-closed', () => { if (!tray) app.quit(); });
}
