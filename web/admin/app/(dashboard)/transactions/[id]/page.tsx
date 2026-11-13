import Link from "next/link";
import { notFound } from "next/navigation";
import { CopyId } from "@/components/CopyId";
import { Badge, Empty, Id, Time } from "@/components/ui";
import { adminGet } from "@/lib/api";
import { money } from "@/lib/format";
import type { TransactionDetail } from "@/lib/types";

export const metadata = { title: "Transaction" };

export default async function TransactionPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const detail = await adminGet<TransactionDetail>(`/transactions/${id}`);
  if (!detail) notFound();
  const { transaction: tx, hold } = detail;

  return (
    <>
      <h1 className="capitalize">{tx.kind}</h1>
      <CopyId id={tx.id} />
      <p className="subtitle">
        <Badge value={tx.status} /> · <Time iso={tx.created_at} /> · client{" "}
        <Link href={`/accounts?client_id=${detail.client_id}`}>
          <code title={detail.client_id}>…{detail.client_id.slice(-8)}</code>
        </Link>
      </p>

      <dl className="facts">
        <dt>Reference</dt>
        <dd>{tx.reference ?? "—"}</dd>
        <dt>Description</dt>
        <dd>{tx.description ?? "—"}</dd>
        {tx.reverses_transaction_id && (
          <>
            <dt>Reverses</dt>
            <dd>
              <Id href={`/transactions/${tx.reverses_transaction_id}`} id={tx.reverses_transaction_id} />
            </dd>
          </>
        )}
        {detail.reversed_by && (
          <>
            <dt>Reversed by</dt>
            <dd>
              <Id href={`/transactions/${detail.reversed_by}`} id={detail.reversed_by} />
            </dd>
          </>
        )}
        {hold && (
          <>
            <dt>Hold</dt>
            <dd>
              <Id id={hold.id} /> <Badge value={hold.status} /> · captured {money(hold.captured_amount, hold.currency)} of{" "}
              {money(hold.amount, hold.currency)}
            </dd>
          </>
        )}
      </dl>

      <h2>Legs</h2>
      <div className="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Account</th>
              <th>Direction</th>
              <th className="num">Amount</th>
              <th className="num">Account entry #</th>
            </tr>
          </thead>
          <tbody>
            {tx.legs.map((leg) => (
              <tr key={leg.account_id}>
                <td>
                  <Id href={`/accounts/${leg.account_id}`} id={leg.account_id} />
                </td>
                <td>{leg.direction}</td>
                <td className="num">{money(leg.amount, tx.currency)}</td>
                <td className="num">{leg.account_seq}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      <h2>Outbox event</h2>
      {detail.events.length === 0 ? (
        <Empty>No event found (events are deleted after the 7-day outbox retention).</Empty>
      ) : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Type</th>
                <th>Written</th>
                <th>Published</th>
                <th className="num">Attempts</th>
                <th>Last error</th>
              </tr>
            </thead>
            <tbody>
              {detail.events.map((event) => (
                <tr key={event.id}>
                  <td>{event.type}</td>
                  <td><Time iso={event.created_at} /></td>
                  <td>{event.published_at ? <Time iso={event.published_at} /> : <Badge value="pending" />}</td>
                  <td className="num">{event.attempts}</td>
                  <td className="wrap">{event.last_error ?? "—"}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </>
  );
}
