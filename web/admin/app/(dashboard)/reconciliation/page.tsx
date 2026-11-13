import Link from "next/link";
import { Badge, Empty, Time } from "@/components/ui";
import { adminGet } from "@/lib/api";
import { duration } from "@/lib/format";
import type { ReconciliationRun } from "@/lib/types";

export const metadata = { title: "Reconciliation" };

export default async function ReconciliationPage() {
  const runs = (await adminGet<ReconciliationRun[]>("/reconciliation/runs", { limit: 50 }))!;

  return (
    <>
      <h1>Reconciliation</h1>
      <p className="subtitle">
        Each run recomputes invariants I1–I8 from the postings (design doc §8.3). It runs nightly in the Worker; run one on demand with{" "}
        <code>docker compose -f deploy/docker-compose.yml run --rm migrate reconcile</code>.
      </p>

      {runs.length === 0 ? (
        <Empty>No runs yet.</Empty>
      ) : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Started</th>
                <th>Status</th>
                <th className="num">Duration</th>
                <th className="num">Accounts checked</th>
                <th className="num">Findings</th>
              </tr>
            </thead>
            <tbody>
              {runs.map((run) => (
                <tr key={run.id}>
                  <td>
                    <Link href={`/reconciliation/${run.id}`}><Time iso={run.started_at} /></Link>
                  </td>
                  <td>
                    <Badge value={run.status} />
                  </td>
                  <td className="num">
                    {run.finished_at
                      ? duration(Math.round((Date.parse(run.finished_at) - Date.parse(run.started_at)) / 1000))
                      : "—"}
                  </td>
                  <td className="num">{run.accounts_checked?.toLocaleString("en-IN") ?? "—"}</td>
                  <td className="num">{run.findings_count ?? "—"}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </>
  );
}
