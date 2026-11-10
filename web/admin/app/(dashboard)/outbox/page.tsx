import Link from "next/link";
import { Empty, Id, Pager } from "@/components/ui";
import { adminGet } from "@/lib/api";
import { dateTime } from "@/lib/format";
import type { OutboxMessage, Page } from "@/lib/types";

export const metadata = { title: "Outbox" };

// Events whose aggregate is a transaction, so the dashboard can link to it.
const transactionEvents = new Set(["transfer.posted", "transfer.reversed", "hold.captured"]);

export default async function OutboxPage({ searchParams }: { searchParams: Promise<{ state?: string; after?: string }> }) {
  const params = await searchParams;
  const state = params.state === "failing" ? "failing" : "unpublished";
  const page = (await adminGet<Page<OutboxMessage>>("/outbox", { state, after: params.after }))!;

  return (
    <>
      <h1>Outbox</h1>
      <p className="subtitle">Events written with each ledger change and waiting for the relay to publish them (design doc §9.2).</p>

      <nav className="tabs">
        <Link href="/outbox" aria-current={state === "unpublished" ? "page" : undefined}>
          Unpublished
        </Link>
        <Link href="/outbox?state=failing" aria-current={state === "failing" ? "page" : undefined}>
          Failing
        </Link>
      </nav>

      {page.items.length === 0 ? (
        <Empty>{state === "failing" ? "Nothing is failing." : "Every event has been published."}</Empty>
      ) : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Type</th>
                <th>Aggregate</th>
                <th>Written</th>
                <th>Next attempt</th>
                <th className="num">Attempts</th>
                <th>Last error</th>
              </tr>
            </thead>
            <tbody>
              {page.items.map((message) => (
                <tr key={message.id}>
                  <td>{message.type}</td>
                  <td>
                    <Id
                      href={transactionEvents.has(message.type) ? `/transactions/${message.aggregate_id}` : undefined}
                      id={message.aggregate_id}
                    />
                  </td>
                  <td>{dateTime(message.created_at)}</td>
                  <td>{dateTime(message.next_attempt_at)}</td>
                  <td className="num">{message.attempts}</td>
                  <td className="wrap">{message.last_error ?? "—"}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      <Pager basePath="/outbox" params={{ state: params.state, after: params.after }} next={page.next_cursor} />
    </>
  );
}
