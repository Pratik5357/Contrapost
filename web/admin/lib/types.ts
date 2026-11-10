// The JSON shapes of /v1/admin/* (snake_case, as the API sends them).

export type Balance = {
  posted: number;
  available: number;
  debits_posted: number;
  credits_posted: number;
  debits_pending: number;
  credits_pending: number;
};

export type Account = {
  id: string;
  code: string;
  currency: string;
  normal_side: "debit" | "credit";
  prevent_overdraft: boolean;
  status: "active" | "frozen" | "closed";
  created_at: string;
  balance: Balance;
};

export type Entry = { transaction_id: string; account_seq: number; direction: "debit" | "credit"; amount: number; created_at: string };

export type EntryPage = { entries: Entry[]; next_cursor: string | null };

export type HoldStatus = "pending" | "partially_captured" | "captured" | "voided" | "expired";

export type Hold = {
  id: string;
  status: HoldStatus;
  debit_account_id: string;
  credit_account_id: string;
  currency: string;
  amount: number;
  captured_amount: number;
  remaining: number;
  expires_at: string;
  created_at: string;
};

export type Leg = { account_id: string; direction: "debit" | "credit"; amount: number; account_seq: number };

export type Transaction = {
  id: string;
  kind: "transfer" | "capture" | "reversal";
  status: string;
  currency: string;
  reference: string | null;
  description: string | null;
  hold_id: string | null;
  reverses_transaction_id: string | null;
  created_at: string;
  legs: Leg[];
};

export type OutboxMessage = {
  id: string;
  aggregate_id: string;
  type: string;
  created_at: string;
  next_attempt_at: string;
  published_at: string | null;
  attempts: number;
  last_error: string | null;
};

export type ReconciliationRun = {
  id: string;
  status: "running" | "clean" | "drift_found" | "failed";
  started_at: string;
  finished_at: string | null;
  accounts_checked: number | null;
  findings_count: number | null;
};

export type Finding = { id: number; check_name: string; subject_id: string | null; expected: unknown; actual: unknown };

export type Page<T> = { items: T[]; next_cursor: string | null };

export type Overview = {
  currencies: { currency: string; accounts: number; debits_posted: number; credits_posted: number; balanced: boolean }[];
  requests_last_hour: { operation: string; status: number; count: number }[];
  outbox: { unpublished: number; failing: number; oldest_unpublished_seconds: number };
  latest_reconciliation: ReconciliationRun | null;
};

export type AdminAccount = { client_id: string; account: Account };

export type AccountDetail = { client_id: string; account: Account; open_holds: Hold[]; recent_entries: EntryPage };

export type AdminHold = { client_id: string; hold: Hold };

export type TransactionDetail = {
  client_id: string;
  transaction: Transaction;
  hold: Hold | null;
  reversed_by: string | null;
  events: OutboxMessage[];
};
