'use strict';
const MAX_URL = 'https://web.max.ru/';
function isMaxUrl(value) {
  try { return new URL(value).origin === new URL(MAX_URL).origin; }
  catch { return false; }
}
function isExternalUrl(value) {
  try { return ['https:', 'http:'].includes(new URL(value).protocol); }
  catch { return false; }
}
module.exports = { MAX_URL, isMaxUrl, isExternalUrl };
