// Load scenarios from design doc §13.2. Run through tests/load/run.sh, which prepares the stack and an API key.
//   SCENARIO  uniform | hot_account | retry_storm | mixed   (default mixed)
//   VUS       concurrent virtual users                      (default 5: light on a laptop)
//   DURATION  e.g. 30s, 2m                                  (default 30s)
import http from 'k6/http';
import { check } from 'k6';
import { Counter } from 'k6/metrics';

const BASE = __ENV.BASE_URL || 'http://localhost:8080';
const SCENARIO = __ENV.SCENARIO || 'mixed';
const WALLETS = Number(__ENV.WALLETS || 200);
const HEADERS = { Authorization: `Bearer ${__ENV.API_KEY}`, 'Content-Type': 'application/json' };

export const options = {
  vus: Number(__ENV.VUS || 5),
  duration: __ENV.DURATION || '30s',
  setupTimeout: '2m',
  summaryTrendStats: ['avg', 'p(50)', 'p(95)', 'p(99)', 'max'],
  thresholds: {
    // Business rejections (409, 422) are expected answers, not failures; see setResponseCallback below.
    http_req_failed: ['rate<0.01'],
    // The alert target from design doc §12.
    'http_req_duration{op:transfer}': ['p(99)<500'],
    replay_mismatches: ['count==0'],
  },
};

http.setResponseCallback(http.expectedStatuses(200, 201, 409, 422));

const replayMismatches = new Counter('replay_mismatches');

function post(path, body, op, key = crypto.randomUUID()) {
  return http.post(`${BASE}${path}`, JSON.stringify(body), {
    headers: { ...HEADERS, 'Idempotency-Key': key },
    tags: { op },
  });
}

function transferBody(from, to, amount) {
  return {
    currency: 'INR',
    legs: [
      { account_id: from, direction: 'debit', amount },
      { account_id: to, direction: 'credit', amount },
    ],
  };
}

const pick = (list) => list[Math.floor(Math.random() * list.length)];
const amount = () => 1 + Math.floor(Math.random() * 5_000);

export function setup() {
  const create = (code, normal_side, prevent_overdraft) => {
    const res = http.post(`${BASE}/v1/accounts`, JSON.stringify({ code, currency: 'INR', normal_side, prevent_overdraft }), { headers: HEADERS });
    if (res.status !== 201) throw new Error(`creating ${code} failed: ${res.status} ${res.body}`);
    return res.json('id');
  };

  const run = Date.now();
  const funding = create(`funding:${run}`, 'debit', false);
  // A system account most hot_account transfers pay into; its balance row is the contended lock.
  const settlement = create(`settlement:${run}`, 'credit', false);
  const wallets = [];
  for (let i = 0; i < WALLETS; i++) {
    const wallet = create(`wallet:${run}:${i}`, 'credit', true);
    // Funded generously, so most transfers post and the run measures writes rather than rejections.
    post('/v1/transfers', transferBody(funding, wallet, 1_000_000_000), 'setup');
    wallets.push(wallet);
  }
  return { wallets, settlement };
}

// Per-VU state for scenarios that revisit earlier requests.
const sent = [];
const holds = [];

export default function (data) {
  switch (SCENARIO) {
    case 'uniform':
      return uniform(data);
    case 'hot_account':
      return Math.random() < 0.8 ? toSettlement(data) : uniform(data);
    case 'retry_storm':
      return Math.random() < 0.3 && sent.length > 0 ? retry() : uniform(data, true);
    default:
      return mixed(data);
  }
}

function uniform({ wallets }, remember = false) {
  const from = pick(wallets);
  let to = pick(wallets);
  while (to === from) to = pick(wallets);
  const body = transferBody(from, to, amount());
  const key = crypto.randomUUID();
  const res = post('/v1/transfers', body, 'transfer', key);
  check(res, { 'transfer posted or refused': (r) => r.status === 201 || r.status === 422 });
  if (remember && sent.length < 500) sent.push({ key, body, status: res.status, response: res.body });
}

function toSettlement({ wallets, settlement }) {
  const res = post('/v1/transfers', transferBody(pick(wallets), settlement, amount()), 'transfer');
  check(res, { 'transfer posted or refused': (r) => r.status === 201 || r.status === 422 });
}

// Re-sends an earlier transfer with the same key and body: it must replay, never post again.
function retry() {
  const original = pick(sent);
  const res = post('/v1/transfers', original.body, 'retry', original.key);
  // Replays come from jsonb, which may reorder keys, so compare canonical forms.
  const replayed = res.status === original.status
    && res.headers['Idempotent-Replayed'] === 'true'
    && canonical(JSON.parse(res.body)) === canonical(JSON.parse(original.response));
  if (!replayed) replayMismatches.add(1);
  check(res, { 'retry replayed the original': () => replayed });
}

function canonical(value) {
  if (Array.isArray(value)) return `[${value.map(canonical).join(',')}]`;
  if (value && typeof value === 'object') {
    return `{${Object.keys(value).sort().map((k) => `${JSON.stringify(k)}:${canonical(value[k])}`).join(',')}}`;
  }
  return JSON.stringify(value);
}

function mixed(data) {
  const roll = Math.random();
  if (roll < 0.6) return uniform(data);
  if (roll < 0.8) {
    const res = post('/v1/holds', {
      debit_account_id: pick(data.wallets), credit_account_id: data.settlement, currency: 'INR',
      amount: amount(), expires_in_seconds: 300,
    }, 'hold');
    check(res, { 'hold created or refused': (r) => r.status === 201 || r.status === 422 });
    if (res.status === 201) holds.push(res.json('id'));
    return;
  }
  if (roll < 0.95 && holds.length > 0) {
    const res = post(`/v1/holds/${holds.pop()}/capture`, {}, 'capture');
    check(res, { 'capture done or refused': (r) => r.status === 200 || r.status === 422 });
    return;
  }
  const res = http.get(`${BASE}/v1/accounts/${pick(data.wallets)}/balance`, { headers: HEADERS, tags: { op: 'read' } });
  check(res, { 'balance read': (r) => r.status === 200 });
}
