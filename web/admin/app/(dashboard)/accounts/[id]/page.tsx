import Link from "next/link";
import { notFound } from "next/navigation";
import { CopyId } from "@/components/CopyId";
import { Badge, Empty, Id, Pager, Time } from "@/components/ui";
import { adminGet } from "@/lib/api";
import { money } from "@/lib/format";
import type { AccountDetail, EntryPage } from "@/lib/types";

export const metadata = { title: "Account" };

export default async function AccountPage({
  params,
  searchParams,
}: {
  params: Promise<{ id: string }>;
  searchParams: Promise<{ after?: string }>;
}) {
  const { id } = await params;
  const { after } = await searchParams;
  const detail = await adminGet<AccountDetail>(`/accounts/${id}`);
  if (!detail) notFound();
  // The detail carries the newest entries; older pages come from the entries endpoint.
  const entries = after ? (await adminGet<EntryPage>(`/accounts/${id}/entries`, { after }))! : detail.recent_entries;
  const { account } = detail;
  const { balance, currency } = account;

  return (
    <>
      <p className="crumbs">
        <Link href="/accounts">Accounts</Link> / {account.currency}
      </p>
      <h1>{account.code}</h1>
      <CopyId id={account.id} />
      <p className="subtitle">
        <Badge value={account.status} /> · {account.normal_side}-normal · {account.prevent_overdraft ? "no overdraft" : "may go negative"} ·
        client <Link href={`/accounts?client_id=${detail.client_id}`}>{detail.client_id}</Link>
      </p>

      <section className="cards">
        <div className="card">
          <div className="caption">Posted</div>
          <div className="value">{money(balance.posted, currency)}</div>
        </div>
        <div className="card">
          <div className="caption">Available</div>
          <div className="value">{money(balance.available, currency)}</div>
          <div className="caption">posted, less money reserved by open holds</div>
        </div>
        <div className="card">
          <div className="caption">Reserved by holds</div>
          <div className="value">{money(account.normal_side === "credit" ? balance.debits_pending : balance.credits_pending, currency)}</div>
          <div className="caption">
            debits {money(balance.debits_pending, currency)} · credits {money(balance.credits_pending, currency)}
          </div>
        </div>
      </section>

      <h2>Open holds</h2>
      {detail.open_holds.length === 0 ? (
        <Empty>No open holds.</Empty>
      ) : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Hold</th>
                <th>Status</th>
                <th>Debit</th>
                <th>Credit</th>
                <th className="num">Remaining</th>
                <th>Expires</th>
              </tr>
            </thead>
            <tbody>
              {detail.open_holds.map((hold) => (
                <tr key={hold.id}>
                  <td>
                    <Id id={hold.id} />
                  </td>
                  <td>
                    <Badge value={hold.status} />
                  </td>
                  <td>
                    <Id href={`/accounts/${hold.debit_account_id}`} id={hold.debit_account_id} />
                  </td>
                  <td>
                    <Id href={`/accounts/${hold.credit_account_id}`} id={hold.credit_account_id} />
                  </td>
                  <td className="num">{money(hold.remaining, hold.currency)}</td>
                  <td><Time iso={hold.expires_at} /></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <h2>Entries</h2>
      {entries.entries.length === 0 ? (
        <Empty>No postings yet.</Empty>
      ) : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th className="num">#</th>
                <th>Transaction</th>
                <th>Direction</th>
                <th className="num">Amount</th>
                <th>Posted at</th>
              </tr>
            </thead>
            <tbody>
              {entries.entries.map((entry) => (
                <tr key={entry.account_seq}>
                  <td className="num">{entry.account_seq}</td>
                  <td>
                    <Id href={`/transactions/${entry.transaction_id}`} id={entry.transaction_id} />
                  </td>
                  <td>{entry.direction}</td>
                  <td className="num">{money(entry.amount, currency)}</td>
                  <td><Time iso={entry.created_at} /></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      <Pager basePath={`/accounts/${id}`} params={{ after }} next={entries.next_cursor} />
    </>
  );
}
