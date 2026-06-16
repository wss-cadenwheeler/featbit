/*
 * Long-running WebSocket load and lifecycle observer for cp09-pod-heartbeats.
 *
 * Environment variables:
 * - WEST_CLIENTS: int, default 10; VUs 1..WEST_CLIENTS connect to west.
 * - EAST_CLIENTS: int, default 20; remaining VUs connect to east.
 * - SDK_TYPE: "server" or "client", default "server".
 * - SERVER_SECRET: string, required when SDK_TYPE=server.
 * - CLIENT_SECRET: string, required when SDK_TYPE=client.
 * - WEST_PORT: int, default 5100.
 * - EAST_PORT: int, default 5101.
 * - HOST: string, default "localhost".
 * - EVENT_LOG_PREFIX: string, default "CP09_EVENT". k6 cannot append to an
 *   arbitrary status file, so lifecycle events are emitted to stdout as:
 *   <prefix> {"ts":...,"vu":...,"cluster":"west|east","event":"...",...}
 * - RUN_DURATION: k6 duration string, default "5m". k6 exits after this duration
 *   unless the process receives SIGTERM first.
 *
 * Standalone smoke test:
 *   k6 run -e WEST_CLIENTS=2 -e EAST_CLIENTS=2 -e SERVER_SECRET=... -e RUN_DURATION=30s cp09-connections.js
 */
import ws from 'k6/ws';
import { check } from 'k6';
import { Counter, Gauge } from 'k6/metrics';
import { textSummary } from 'https://jslib.k6.io/k6-summary/0.0.4/index.js';

import { generateConnectionToken } from '../utils.js';

const WEST_CLIENTS = parseIntegerEnv('WEST_CLIENTS', 10);
const EAST_CLIENTS = parseIntegerEnv('EAST_CLIENTS', 20);
const SDK_TYPE = (__ENV.SDK_TYPE || 'server').toLowerCase();
const SERVER_SECRET = __ENV.SERVER_SECRET || '';
const CLIENT_SECRET = __ENV.CLIENT_SECRET || '';
const WEST_PORT = parseIntegerEnv('WEST_PORT', 5100);
const EAST_PORT = parseIntegerEnv('EAST_PORT', 5101);
const HOST = __ENV.HOST || 'localhost';
const EVENT_LOG_PREFIX = __ENV.EVENT_LOG_PREFIX || 'CP09_EVENT';
const RUN_DURATION = __ENV.RUN_DURATION || '5m';

const MAX_RECONNECT_ATTEMPTS = 3;
const PING_INTERVAL_MS = 18 * 1000; // Mirrors benchmark/k6-scripts/data-sync.js.
const RUN_DURATION_MS = parseDurationMs(RUN_DURATION);

if (SDK_TYPE !== 'server' && SDK_TYPE !== 'client') {
  throw new Error(`SDK_TYPE must be "server" or "client"; got "${SDK_TYPE}"`);
}

if (WEST_CLIENTS + EAST_CLIENTS <= 0) {
  throw new Error('WEST_CLIENTS + EAST_CLIENTS must be greater than 0');
}

const CONNECTION_SECRET = SDK_TYPE === 'server' ? SERVER_SECRET : CLIENT_SECRET;
const REQUIRED_SECRET_NAME = SDK_TYPE === 'server' ? 'SERVER_SECRET' : 'CLIENT_SECRET';

if (!CONNECTION_SECRET) {
  throw new Error(`${REQUIRED_SECRET_NAME} is required when SDK_TYPE=${SDK_TYPE}`);
}

export const options = {
  scenarios: {
    cp09: {
      executor: 'per-vu-iterations',
      vus: WEST_CLIENTS + EAST_CLIENTS,
      iterations: 1,
      maxDuration: RUN_DURATION,
    },
  },
  thresholds: {
    cp09_open_failures: ['count==0'],
  },
};

const opensCounter = new Counter('cp09_opens');
const closesCounter = new Counter('cp09_closes');
const reconnectsCounter = new Counter('cp09_reconnects');
const openFailuresCounter = new Counter('cp09_open_failures');
const messagesCounter = new Counter('cp09_data_sync_pushes');
const activeConnections = new Gauge('cp09_active_connections');

const PING_MESSAGE = JSON.stringify({
  messageType: 'ping',
  data: {},
});

const PONG_MESSAGE = JSON.stringify({
  messageType: 'pong',
  data: {},
});

export default function () {
  const vu = __VU;
  const cluster = vu <= WEST_CLIENTS ? 'west' : 'east';
  const port = cluster === 'west' ? WEST_PORT : EAST_PORT;
  const runStartedAt = Date.now();
  let activeForVu = 0;
  let reconnectAttempts = 0;

  function remainingRunMs() {
    return Math.max(0, RUN_DURATION_MS - (Date.now() - runStartedAt));
  }

  function withinRunDuration() {
    return remainingRunMs() > 0;
  }

  function recordActiveConnectionDelta(delta) {
    activeForVu = Math.max(0, activeForVu + delta);
    activeConnections.add(activeForVu);
  }

  function emit(event, code, details, extra) {
    emitEvent(vu, cluster, event, code, details, extra);
  }

  function connect(reconnectAttempt) {
    const url = buildStreamingUrl(port);
    let opened = false;

    const response = ws.connect(url, {}, function (socket) {
      let pingInterval = null;
      let runTimeout = null;

      socket.on('open', function () {
        opened = true;
        opensCounter.add(1);
        recordActiveConnectionDelta(1);
        emit('open', null, reconnectAttempt > 0 ? `attempt=${reconnectAttempt}` : 'initial');

        if (reconnectAttempt > 0) {
          reconnectsCounter.add(1);
          emit('reconnect', null, `attempt=${reconnectAttempt}`);
        }

        socket.send(buildDataSyncHandshake(vu));

        pingInterval = socket.setInterval(function () {
          socket.send(PING_MESSAGE);
        }, PING_INTERVAL_MS);

        const remaining = remainingRunMs();
        if (remaining > 0) {
          runTimeout = socket.setTimeout(function () {
            socket.close();
          }, remaining);
        }
      });

      socket.on('message', function (rawMessage) {
        let message;
        try {
          message = JSON.parse(normalizeSocketMessage(rawMessage));
        } catch (error) {
          emit('error', null, `invalid json: ${formatError(error)}`);
          return;
        }

        if (message.messageType === 'data-sync') {
          messagesCounter.add(1);
          emit('message', null, describeDataSyncMessage(message));
          return;
        }

        if (message.messageType === 'ping') {
          socket.send(PONG_MESSAGE);
          return;
        }

        if (message.messageType === 'pong') {
          return;
        }
      });

      socket.on('close', function (closeEvent) {
        if (pingInterval !== null) {
          socket.clearInterval(pingInterval);
        }
        if (runTimeout !== null) {
          socket.clearTimeout(runTimeout);
        }

        if (opened) {
          recordActiveConnectionDelta(-1);
        }

        const code = extractCloseCode(closeEvent);
        const wasUnexpected = code !== 1000;
        closesCounter.add(1);
        emit('close', code, `wasUnexpected=${wasUnexpected}`, { wasUnexpected });

        if (wasUnexpected && withinRunDuration() && reconnectAttempts < MAX_RECONNECT_ATTEMPTS) {
          reconnectAttempts += 1;
          connect(reconnectAttempts);
          return;
        }

        if (wasUnexpected && withinRunDuration() && reconnectAttempts >= MAX_RECONNECT_ATTEMPTS) {
          emit('error', code, `max reconnect attempts reached (${MAX_RECONNECT_ATTEMPTS})`);
        }
      });

      socket.on('error', function (error) {
        openFailuresCounter.add(1);
        emit('error', null, formatError(error));
      });
    });

    check(response, {
      'cp09 websocket upgrade succeeded': (res) => res && res.status === 101,
    });

    if (!response || response.status !== 101) {
      openFailuresCounter.add(1);
      emit('error', response ? response.status : null, 'websocket upgrade failed');
    }
  }

  connect(0);
}

function buildStreamingUrl(port) {
  const token = encodeURIComponent(generateConnectionToken(CONNECTION_SECRET));
  return `ws://${HOST}:${port}/streaming?type=${SDK_TYPE}&version=2&token=${token}`;
}

function buildDataSyncHandshake(vu) {
  if (SDK_TYPE === 'server') {
    return JSON.stringify({
      messageType: 'data-sync',
      data: {
        timestamp: 0,
      },
    });
  }

  return JSON.stringify({
    messageType: 'data-sync',
    data: {
      user: {
        keyId: `cp09-vu-${vu}`,
        name: `cp09 vu ${vu}`,
      },
      timestamp: 0,
    },
  });
}

function emitEvent(vu, cluster, event, code, details, extra) {
  const eventObject = Object.assign({
    ts: Date.now(),
    vu,
    cluster,
    event,
    code: code === undefined ? null : code,
    details: details || '',
  }, extra || {});

  console.log(`${EVENT_LOG_PREFIX} ${JSON.stringify(eventObject)}`);
}

function normalizeSocketMessage(rawMessage) {
  if (typeof rawMessage === 'string') {
    return rawMessage;
  }

  if (rawMessage && typeof rawMessage.data === 'string') {
    return rawMessage.data;
  }

  return String(rawMessage);
}

function describeDataSyncMessage(message) {
  const data = message.data || {};
  const eventType = data.eventType || 'unknown';
  const userKeyId = data.userKeyId ? ` userKeyId=${data.userKeyId}` : '';
  return `messageType=data-sync eventType=${eventType}${userKeyId}`;
}

function extractCloseCode(closeEvent) {
  if (typeof closeEvent === 'number') {
    return closeEvent;
  }

  if (closeEvent && typeof closeEvent.code === 'number') {
    return closeEvent.code;
  }

  return null;
}

function formatError(error) {
  if (!error) {
    return 'unknown error';
  }

  if (typeof error === 'string') {
    return error;
  }

  if (typeof error.error === 'function') {
    return error.error();
  }

  if (error.message) {
    return error.message;
  }

  try {
    return JSON.stringify(error);
  } catch (_) {
    return String(error);
  }
}

function parseIntegerEnv(name, defaultValue) {
  const raw = __ENV[name];
  if (raw === undefined || raw === '') {
    return defaultValue;
  }

  const parsed = parseInt(raw, 10);
  if (Number.isNaN(parsed) || parsed < 0) {
    throw new Error(`${name} must be a non-negative integer; got "${raw}"`);
  }

  return parsed;
}

function parseDurationMs(duration) {
  const source = String(duration).trim();
  const pattern = /(\d+(?:\.\d+)?)(ms|s|m|h)/g;
  const multipliers = {
    ms: 1,
    s: 1000,
    m: 60 * 1000,
    h: 60 * 60 * 1000,
  };
  let total = 0;
  let consumed = '';
  let match;

  while ((match = pattern.exec(source)) !== null) {
    total += parseFloat(match[1]) * multipliers[match[2]];
    consumed += match[0];
  }

  if (total <= 0 || consumed !== source) {
    throw new Error(`RUN_DURATION must be a k6 duration string like "30s" or "5m"; got "${duration}"`);
  }

  return total;
}

export function handleSummary(data) {
  return { stdout: textSummary(data, { indent: ' ', enableColors: false }) };
}
