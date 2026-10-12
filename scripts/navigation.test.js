'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { isMaxUrl, isExternalUrl } = require('../src/navigation');
test('only the official HTTPS MAX origin stays inside the client', () => {
  assert.equal(isMaxUrl('https://web.max.ru/'), true);
  assert.equal(isMaxUrl('https://web.max.ru/chat/123'), true);
  for (const url of ['http://web.max.ru/', 'https://web.max.ru.evil.example/', 'https://web.max.ru@evil.example/', 'javascript:alert(1)', 'file:///etc/passwd', 'invalid']) {
    assert.equal(isMaxUrl(url), false, url);
  }
});
test('external links cannot launch arbitrary local protocols', () => {
  assert.equal(isExternalUrl('https://example.com'), true);
  for (const url of ['file:///C:/Windows', 'javascript:alert(1)', 'cmd:calc', 'invalid']) assert.equal(isExternalUrl(url), false);
});
