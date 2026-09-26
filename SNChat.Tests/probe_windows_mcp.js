// Ask windows-system-mcp what it actually exposes.
//
// The integration guide lists tool names like "file_list" and "process_list".
// Those are guesses; this prints the real list so the config and the docs can
// be written against what the server really does.
//
//   node SNChat.Tests/probe_windows_mcp.js

const { spawn } = require('child_process');

const server = spawn('npx', ['-y', 'windows-system-mcp'], {
  stdio: ['pipe', 'pipe', 'pipe'],
  shell: true,
});

let buffer = '';
const pending = new Map();
let nextId = 1;

server.stdout.on('data', (chunk) => {
  buffer += chunk.toString();

  let newline;
  while ((newline = buffer.indexOf('\n')) >= 0) {
    const line = buffer.slice(0, newline).trim();
    buffer = buffer.slice(newline + 1);
    if (!line) continue;

    let message;
    try {
      message = JSON.parse(line);
    } catch {
      continue; // banner text on stdout, not a JSON-RPC frame
    }

    const resolve = pending.get(message.id);
    if (resolve) {
      pending.delete(message.id);
      resolve(message);
    }
  }
});

server.stderr.on('data', (d) => process.stderr.write('[server] ' + d));

function send(method, params) {
  const id = nextId++;
  return new Promise((resolve, reject) => {
    pending.set(id, resolve);
    server.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
    setTimeout(() => reject(new Error('timeout on ' + method)), 60000);
  });
}

function notify(method, params) {
  server.stdin.write(JSON.stringify({ jsonrpc: '2.0', method, params }) + '\n');
}

(async () => {
  const init = await send('initialize', {
    protocolVersion: '2024-11-05',
    clientInfo: { name: 'probe', version: '1.0.0' },
    capabilities: {},
  });

  console.log('serverInfo:', JSON.stringify(init.result.serverInfo));
  console.log('protocolVersion:', init.result.protocolVersion);
  console.log('capabilities:', JSON.stringify(init.result.capabilities));

  notify('notifications/initialized', {});

  const tools = await send('tools/list', {});
  const list = tools.result.tools;

  console.log('\ntool count:', list.length);
  for (const tool of list) {
    const required = (tool.inputSchema && tool.inputSchema.required) || [];
    const props = Object.keys((tool.inputSchema && tool.inputSchema.properties) || {});
    console.log(
      `\n- ${tool.name}` +
      `\n    desc: ${(tool.description || '').split('\n')[0]}` +
      `\n    props: ${props.join(', ') || '(none)'}` +
      `\n    required: ${required.join(', ') || '(none)'}`
    );
  }

  console.log('\n--- actions per tool ---');
  for (const tool of list) {
    const action = tool.inputSchema?.properties?.action;
    console.log(`${tool.name}: ${(action?.enum || []).join(', ')}`);
  }

  server.kill();
  process.exit(0);
})().catch((e) => {
  console.error('probe failed:', e.message);
  server.kill();
  process.exit(1);
});
