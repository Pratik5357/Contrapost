import Link from "next/link";
import { notFound } from "next/navigation";
import { Badge, Empty } from "@/components/ui";
import { adminGet } from "@/lib/api";
import { dateTime } from "@/lib/format";
import type { Finding, ReconciliationRun } from "@/lib/types";

export const metadata = { title: "Reconciliation run" };

export default async function RunPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const findings = await adminGet<Finding[]>(`/reconciliation/runs/${id}/findings`, { limit: 1000 });
  if (!findings) notFound();
  // There is no single-run endpoint; the run is among the latest ones the dashboard lists.
  const run = (await adminGet<ReconciliationRun[]>("/reconciliation/runs", { limit: 100 }))!.find((r) => r.id === id);

  return (
    <>
      <p>
        <Link href="/reconciliation">← All runs</Link>
      </p>
      <h1>Reconciliation run</h1>
      <p className="subtitle">
        {run ? (
          <>
            <Badge value={run.status} /> · started {dateTime(run.started_at)} · {run.accounts_checked ?? 0} accounts checked
          </>
        ) : (
          <code>{id}</code>
        )}
      </p>

      {findings.length === 0 ? (
        <Empty>No findings: the ledger agreed with itself.</Empty>
      ) : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Check</th>
                <th>Subject</th>
                <th>Expected</th>
                <th>Actual</th>
              </tr>
            </thead>
            <tbody>
              {findings.map((finding) => (
                <tr key={finding.id}>
                  <td>{finding.check_name}</td>
                  <td>{finding.subject_id ? <code title={finding.subject_id}>{finding.subject_id}</code> : "—"}</td>
                  <td className="wrap">
                    <pre>{JSON.stringify(finding.expected, null, 1)}</pre>
                  </td>
                  <td className="wrap">
                    <pre>{JSON.stringify(finding.actual, null, 1)}</pre>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </>
  );
}
