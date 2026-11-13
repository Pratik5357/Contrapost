import Link from "next/link";
import { LiveStatus } from "@/components/LiveStatus";
import { Badge, Empty, Time } from "@/components/ui";
import { adminGet } from "@/lib/api";
import { duration, money } from "@/lib/format";
import type { Overview } from "@/lib/types";

export const metadata = { title: "Overview" };

export default async function OverviewPage() {
  const overview = (await adminGet<Overview>("/overview"))!;
  const { outbox, latest_reconciliation: run } = overview;
  const operations = [...new Set(overview.requests_last_hour.map((r) => r.operation))];
  const statuses = [...new Set(overview.requests_last_hour.map((r) => r.status))].sort();
  const count = (operation: string, status: number) =>
    overview.requests_last_hour.find((r) => r.operation === operation && r.status === status)?.count ?? 0;

  return (
    <>
      <header className="page-header">
        <div>
          <h1>Overview</h1>
          <p className="subtitle">The ledger at a glance: money in balance, events flowing, the last self-check.</p>
        </div>
        <LiveStatus seconds={5} renderedAt={Date.now()} />
      </header>

      <section className="cards">
        <div className="card">
          <div className="caption">Unpublished events</div>
          <div className="value">{outbox.unpublished.toLocaleString("en-IN")}</div>
          <div className="caption">
            {outbox.unpublished > 0 ? `oldest is ${duration(outbox.oldest_unpublished_seconds)} old` : "all events published"}
          </div>
        </div>
        <div className="card">
          <div className="caption">Failing events</div>
          <div className="value">{outbox.failing.toLocaleString("en-IN")}</div>
          <div className="caption">
            {outbox.failing > 0 ? <Link href="/outbox?state=failing">see what&apos;s failing →</Link> : "nothing is retrying"}
          </div>
        </div>
        <div className="card">
          <div className="caption">Latest reconciliation</div>
          <div className="value">{run ? <Badge value={run.status} /> : "—"}</div>
          <div className="caption">
            {run ? (
              <Link href={`/reconciliation/${run.id}`}>
                <Time iso={run.started_at} /> · {run.findings_count ?? 0} findings →
              </Link>
            ) : (
              "no runs yet"
            )}
          </div>
        </div>
      </section>

      <h2>Totals per currency</h2>
      {overview.currencies.length === 0 ? (
        <Empty>No accounts yet.</Empty>
      ) : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Currency</th>
                <th className="num">Accounts</th>
                <th className="num">Debits posted</th>
                <th className="num">Credits posted</th>
                <th title="Invariant I2: across a currency, posted debits equal posted credits">Zero-sum (I2)</th>
              </tr>
            </thead>
            <tbody>
              {overview.currencies.map((c) => (
                <tr key={c.currency}>
                  <td>{c.currency}</td>
                  <td className="num">{c.accounts.toLocaleString("en-IN")}</td>
                  <td className="num">{money(c.debits_posted, c.currency)}</td>
                  <td className="num">{money(c.credits_posted, c.currency)}</td>
                  <td>{c.balanced ? <Badge value="clean" /> : <Badge value="drift_found" />}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <h2>Requests in the last hour</h2>
      {operations.length === 0 ? (
        <Empty>No money-moving requests in the last hour.</Empty>
      ) : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Operation</th>
                {statuses.map((s) => (
                  <th key={s} className="num">
                    {s}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {operations.map((operation) => (
                <tr key={operation}>
                  <td>{operation}</td>
                  {statuses.map((s) => (
                    <td key={s} className="num">
                      {count(operation, s).toLocaleString("en-IN")}
                    </td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      <p className="muted">
        From the stored idempotency keys, so 400s and 5xx (which aren&apos;t stored) are not counted.
        {process.env.GRAFANA_URL && (
          <>
            {" "}
            Throughput and latency are in{" "}
            <a href={process.env.GRAFANA_URL} target="_blank" rel="noreferrer">
              Grafana
            </a>
            .
          </>
        )}
      </p>
    </>
  );
}
