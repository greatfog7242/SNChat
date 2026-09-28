// Ask chrome-devtools-mcp what it actually exposes.
//
// Lists all 30 tools the server provides so we can verify that the guard
// correctly categorizes them into observational, navigation, interaction,
// and high-risk buckets.
//
//   node SNChat.Tests/probe_chrome_mcp.js

const { spawn } = require('child_process');

const server = spawn('npx', [
  '-y', 'chrome-devtools-mcp@latest',
  '--isolated',
  '--headless',
  '--no-usage-statistics',
  '--no-performance-crux'
], {
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
    setTimeout(() => reject(new Error('timeout on ' + method)), 90000);
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

  // Categorize tools by their apparent risk/function
  const observational = [];
  const navigation = [];
  const interaction = [];
  const highRisk = [];

  for (const tool of list) {
    const name = tool.name;
    if (['list_pages', 'select_page', 'take_snapshot', 'take_screenshot',
         'get_css_styles', 'list_console_messages', 'get_console_message',
         'list_network_requests', 'get_network_request',
         'performance_start_trace', 'performance_stop_trace',
         'performance_analyze_insight', 'lighthouse_audit',
         'take_heapsnapshot', 'wait_for', 'resize_page'].includes(name)) {
      observational.push(name);
    } else if (['navigate_page', 'new_page', 'close_page'].includes(name)) {
      navigation.push(name);
    } else if (['click', 'fill', 'fill_form', 'type_text', 'press_key',
                'drag', 'hover', 'handle_dialog'].includes(name)) {
      interaction.push(name);
    } else if (['evaluate_script', 'emulate', 'upload_file'].includes(name)) {
      highRisk.push(name);
    }
  }

  console.log('\n=== Tool Categorization ===');
  console.log(`\nObservational (${observational.length}):`);
  observational.forEach(name => console.log(`  - ${name}`));

  console.log(`\nNavigation (${navigation.length}):`);
  navigation.forEach(name => console.log(`  - ${name}`));

  console.log(`\nInteraction (${interaction.length}):`);
  interaction.forEach(name => console.log(`  - ${name}`));

  console.log(`\nHigh Risk (${highRisk.length}):`);
  highRisk.forEach(name => console.log(`  - ${name}`));

  const uncategorized = list
    .map(t => t.name)
    .filter(name =>
      !observational.includes(name) &&
      !navigation.includes(name) &&
      !interaction.includes(name) &&
      !highRisk.includes(name)
    );

  if (uncategorized.length > 0) {
    console.log(`\nUNCATEGORIZED (${uncategorized.length}):`);
    uncategorized.forEach(name => console.log(`  - ${name}`));
  }

  console.log('\n=== All Tools (detailed) ===');
  for (const tool of list) {
    const required = (tool.inputSchema && tool.inputSchema.required) || [];
    const props = Object.keys((tool.inputSchema && tool.inputSchema.properties) || {});
    console.log(
      `\n- ${tool.name}` +
      `\n    desc: ${(tool.description || '').split('\n')[0]}` +
      `\n    props: ${props.join(', ') || '(none)'}` +
      `\n    required: ${required.join(', ') || '(none)'}`
    );

    // Highlight file path parameters
    const fileParams = props.filter(p =>
      p.includes('path') || p.includes('Path') || p.includes('file') || p.includes('dir')
    );
    if (fileParams.length > 0) {
      console.log(`    FILE PARAMS: ${fileParams.join(', ')}`);
    }
  }

  server.kill();
  process.exit(0);
})().catch((e) => {
  console.error('probe failed:', e.message);
  server.kill();
  process.exit(1);
});
