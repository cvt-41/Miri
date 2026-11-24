// Miri model benchmark: chat (EN/VI language matching), tool calling, vision.
// Usage: node bench.mjs qwen3.5:4b gemma4:e4b qwen3:4b
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const HOST = 'http://127.0.0.1:11434';
const MAX_TOKENS = 500;

const PERSONA = `You are Miri, a calm, slightly grumpy but helpful white pixel cat who lives on the user's desktop.
Keep answers short (under 120 words). ALWAYS reply in the same language the user wrote in (Vietnamese or English).`;

const CHAT_TESTS = [
  { id: 'en-coffee', lang: 'en', q: 'How should I prepare my coffee?' },
  { id: 'vi-coffee', lang: 'vi', q: 'Tôi nên pha cà phê phin như thế nào cho ngon?' },
  { id: 'en-kitten', lang: 'en', q: 'What type of kitten should I adopt? I live in a small apartment and work from home.' },
  { id: 'vi-kitten', lang: 'vi', q: 'Tôi sống ở chung cư nhỏ và đi làm cả ngày, nên nhận nuôi loại mèo nào?' },
];

const UI_SNAPSHOT = `Active window: "Telegram - Chat with Lan"
[1] Edit "Search"
[2] ListItem "Lan"
[3] ListItem "Minh"
[4] Edit "Write a message..."
[5] Button "Send"
[6] Button "Attach file"`;

const AGENT_SYSTEM = `You are Miri, a desktop cat that can operate apps for the user using tools.
Here is the current UI of the active window (element ids in brackets):
${UI_SNAPSHOT}
Rules: use the element ids above. Only call a tool when the user asks you to DO something in the app.
For plain questions, just answer in text without tools.`;

const TOOLS = [
  fn('click', 'Click a UI element by its id', { id: { type: 'integer', description: 'Element id from the snapshot' } }, ['id']),
  fn('type_text', 'Type text into a UI element', { id: { type: 'integer' }, text: { type: 'string' } }, ['id', 'text']),
  fn('press_keys', 'Press a key combination, e.g. "ctrl+s"', { keys: { type: 'string' } }, ['keys']),
];
function fn(name, description, properties, required) {
  return { type: 'function', function: { name, description, parameters: { type: 'object', properties, required } } };
}

// expect: list of acceptable first calls; null = must NOT call a tool
const TOOL_TESTS = [
  { id: 'en-type', q: 'Type "see you at 7" in the message box', expect: [{ name: 'type_text', id: 4, textIncludes: 'see you at 7' }] },
  { id: 'vi-type', q: 'Gõ "xin chào" vào ô tin nhắn', expect: [{ name: 'type_text', id: 4, textIncludes: 'xin chào' }] },
  { id: 'en-click', q: 'Open the chat with Minh', expect: [{ name: 'click', id: 3 }] },
  { id: 'vi-click', q: 'Bấm nút đính kèm tệp giúp tôi', expect: [{ name: 'click', id: 6 }] },
  { id: 'no-tool', q: 'What is the capital of France?', expect: null },
];

const VI_CHARS = /[ăâđêôơưạảấầẩẫậắằẳẵặẹẻẽếềểễệỉịọỏốồổỗộớờởỡợụủứừửữựỳỵỷỹ]/gi;
const CJK = /[぀-ヿ一-鿿]/;

async function chat(model, messages, extra = {}) {
  const body = { model, messages, stream: true, think: false, keep_alive: '10m',
    options: { num_predict: MAX_TOKENS, temperature: 0.3 }, ...extra };
  const t0 = Date.now();
  let res = await fetch(`${HOST}/api/chat`, { method: 'POST', body: JSON.stringify(body) });
  if (!res.ok) {
    const err = await res.text();
    if (/think/i.test(err)) { delete body.think; res = await fetch(`${HOST}/api/chat`, { method: 'POST', body: JSON.stringify(body) }); }
    else throw new Error(`${res.status} ${err}`);
  }
  let content = '', toolCalls = [], final = null, buf = '';
  const decoder = new TextDecoder();
  for await (const chunk of res.body) {
    buf += decoder.decode(chunk, { stream: true });
    let nl;
    while ((nl = buf.indexOf('\n')) >= 0) {
      const line = buf.slice(0, nl).trim(); buf = buf.slice(nl + 1);
      if (!line) continue;
      const j = JSON.parse(line);
      if (j.error) throw new Error(j.error);
      if (j.message?.content) content += j.message.content;
      if (j.message?.tool_calls) toolCalls.push(...j.message.tool_calls);
      if (j.done) final = j;
    }
  }
  const ns = 1e9;
  return {
    content: content.trim(), toolCalls,
    wallSec: (Date.now() - t0) / 1000,
    loadSec: (final?.load_duration ?? 0) / ns,
    promptSec: (final?.prompt_eval_duration ?? 0) / ns,
    tokens: final?.eval_count ?? 0,
    tokPerSec: final?.eval_duration ? final.eval_count / (final.eval_duration / ns) : 0,
  };
}

function checkTool(test, calls) {
  if (test.expect === null) return { pass: calls.length === 0, note: calls.length ? `called ${calls[0].function.name}` : 'no tool (correct)' };
  if (!calls.length) return { pass: false, note: 'no tool call' };
  const c = calls[0].function; const a = typeof c.arguments === 'string' ? JSON.parse(c.arguments) : c.arguments;
  const ok = test.expect.some(e => e.name === c.name && Number(a.id) === e.id &&
    (!e.textIncludes || String(a.text ?? '').toLowerCase().includes(e.textIncludes)));
  return { pass: ok, note: `${c.name}(${JSON.stringify(a)})${calls.length > 1 ? ` +${calls.length - 1} more` : ''}` };
}

async function capabilities(model) {
  const r = await fetch(`${HOST}/api/show`, { method: 'POST', body: JSON.stringify({ model }) });
  if (!r.ok) throw new Error(`model ${model} not found`);
  return (await r.json()).capabilities ?? [];
}

async function unload(model) {
  await fetch(`${HOST}/api/generate`, { method: 'POST', body: JSON.stringify({ model, keep_alive: 0 }) });
}

async function benchModel(model) {
  const caps = await capabilities(model);
  const r = { model, caps, warmup: null, chat: [], tools: [], vision: null };
  console.log(`\n=== ${model}  [${caps.join(', ')}]`);

  r.warmup = await chat(model, [{ role: 'user', content: 'Say hi in 3 words.' }]);
  console.log(`warm-up: load ${r.warmup.loadSec.toFixed(1)}s`);

  for (const t of CHAT_TESTS) {
    const out = await chat(model, [{ role: 'system', content: PERSONA }, { role: 'user', content: t.q }]);
    const viCount = (out.content.match(VI_CHARS) || []).length;
    const gotLang = viCount > 5 ? 'vi' : 'en';
    out.langOk = gotLang === t.lang && !CJK.test(out.content);
    out.note = CJK.test(out.content) ? 'contains CJK characters' : `replied ${gotLang}`;
    r.chat.push({ ...t, ...out });
    console.log(`chat ${t.id}: ${out.langOk ? 'OK ' : 'BAD'} ${out.tokPerSec.toFixed(1)} tok/s, ${out.wallSec.toFixed(1)}s`);
  }

  if (caps.includes('tools')) {
    for (const t of TOOL_TESTS) {
      const out = await chat(model, [{ role: 'system', content: AGENT_SYSTEM }, { role: 'user', content: t.q }], { tools: TOOLS });
      Object.assign(out, checkTool(t, out.toolCalls));
      r.tools.push({ ...t, ...out });
      console.log(`tool ${t.id}: ${out.pass ? 'PASS' : 'FAIL'} ${out.note} (${out.wallSec.toFixed(1)}s)`);
    }
  }

  const img = path.join(HERE, 'dialog.png');
  if (caps.includes('vision') && fs.existsSync(img)) {
    const b64 = fs.readFileSync(img).toString('base64');
    const out = await chat(model, [{ role: 'user', content: 'List every button in this dialog, then say which one saves the file. Answer in English, briefly.', images: [b64] }]);
    const low = out.content.toLowerCase();
    out.found = ['save', "don't save", 'cancel'].filter(b => low.includes(b) || low.includes(b.replace("'", '’')));
    out.pass = out.found.length === 3;
    r.vision = out;
    console.log(`vision: ${out.pass ? 'PASS' : 'PARTIAL'} found [${out.found.join(', ')}] (${out.wallSec.toFixed(1)}s, prompt ${out.promptSec.toFixed(1)}s)`);
  }

  await unload(model);
  return r;
}

function report(results) {
  const L = ['# Miri model benchmark', '', `Run: ${new Date().toISOString()}`, ''];
  L.push('| Model | Load s | Avg tok/s | Lang match | Tool calls | Vision | Avg chat s |', '|---|---|---|---|---|---|---|');
  for (const r of results) {
    if (r.error) { L.push(`| ${r.model} | error: ${r.error} | | | | | |`); continue; }
    const avg = a => a.reduce((s, x) => s + x, 0) / (a.length || 1);
    L.push(`| ${r.model} | ${r.warmup.loadSec.toFixed(1)} | ${avg(r.chat.map(c => c.tokPerSec)).toFixed(1)} | ` +
      `${r.chat.filter(c => c.langOk).length}/${r.chat.length} | ` +
      `${r.tools.length ? `${r.tools.filter(t => t.pass).length}/${r.tools.length}` : 'n/a'} | ` +
      `${r.vision ? `${r.vision.found.length}/3 (${r.vision.wallSec.toFixed(0)}s)` : 'n/a'} | ${avg(r.chat.map(c => c.wallSec)).toFixed(1)} |`);
  }
  for (const r of results) {
    if (r.error) continue;
    L.push('', `## ${r.model}`, '');
    for (const c of r.chat) L.push(`### ${c.id} — ${c.langOk ? 'OK' : 'BAD'} (${c.note}, ${c.tokPerSec.toFixed(1)} tok/s, ${c.wallSec.toFixed(1)}s)`, `> ${c.q}`, '', c.content, '');
    if (r.tools.length) { L.push('### Tool calls'); for (const t of r.tools) L.push(`- **${t.id}** ${t.pass ? 'PASS' : 'FAIL'}: ${t.note}${t.content ? ` — text: "${t.content.slice(0, 120).replace(/\n/g, ' ')}"` : ''} (${t.wallSec.toFixed(1)}s)`); L.push(''); }
    if (r.vision) L.push(`### Vision (${r.vision.wallSec.toFixed(1)}s)`, '', r.vision.content, '');
  }
  return L.join('\n');
}

const models = process.argv.slice(2);
const results = [];
for (const m of models) {
  try { results.push(await benchModel(m)); }
  catch (e) { console.log(`${m}: ERROR ${e.message}`); results.push({ model: m, error: e.message }); }
}
fs.writeFileSync(path.join(HERE, 'results.json'), JSON.stringify(results, null, 2));
fs.writeFileSync(path.join(HERE, 'RESULTS.md'), report(results));
console.log('\nWrote bench/RESULTS.md');
